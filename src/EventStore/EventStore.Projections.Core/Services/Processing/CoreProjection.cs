// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Text;
using EventStore.Common.Log;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Utils;

namespace EventStore.Projections.Core.Services.Processing
{
    //TODO: replace Console.WriteLine with logging
    //TODO: separate check-pointing from projection handling
    public class CoreProjection : IDisposable,
                                  ICoreProjection,
                                  IHandle<ProjectionMessage.Projections.CheckpointCompleted>,
                                  IHandle<ProjectionMessage.Projections.PauseRequested>,
                                  IHandle<ProjectionMessage.SubscriptionMessage.CommittedEventReceived>,
                                  IHandle<ProjectionMessage.SubscriptionMessage.CheckpointSuggested>,
                                  IHandle<ProjectionMessage.SubscriptionMessage.ProgressChanged>
    {
        internal const string ProjectionsStreamPrefix = "$projections-";
        private const string ProjectionsStateStreamSuffix = "-state";
        internal const string ProjectionCheckpointStreamSuffix = "-checkpoint";

        [Flags]
        private enum State : uint
        {
            Initial = 0x80000000,
            LoadStateRequsted = 0x1,
            StateLoadedSubscribed = 0x2,
            Running = 0x08,
            Paused = 0x10,
            Resumed = 0x20,
            Stopping = 0x40,
            Stopped = 0x80,
            FaultedStopping = 0x100,
            Faulted = 0x200,
        }

        private readonly string _name;
        private readonly string _stateStreamNamePattern;
        private readonly string _stateStreamName;

        private readonly IPublisher _publisher;

        private readonly Guid _projectionCorrelationId;
        private readonly ProjectionConfig _projectionConfig;
        private readonly EventFilter _eventFilter;
        private readonly CheckpointStrategy _checkpointStrategy;
        private readonly ILogger _logger;

        private readonly IProjectionStateHandler _projectionStateHandler;
        private State _state;

        private string _faultedReason;

        private readonly
            RequestResponseDispatcher
                <ClientMessage.ReadStreamEventsBackward, ClientMessage.ReadStreamEventsBackwardCompleted>
            _readDispatcher;

        private readonly RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted>
            _writeDispatcher;

        private string _handlerPartition;
        private readonly PartitionStateCache _partitionStateCache;
        private readonly CoreProjectionQueue _processingQueue;
        private readonly CoreProjectionCheckpointManager _checkpointManager;

        private bool _tickPending;
        private int _readRequestsInProgress;
        private long _expectedSubscriptionMessageSequenceNumber = -1;
        private readonly HashSet<Guid> _loadStateRequests = new HashSet<Guid>();

        public CoreProjection(
            string name, Guid projectionCorrelationId, IPublisher publisher,
            IProjectionStateHandler projectionStateHandler, ProjectionConfig projectionConfig,
            RequestResponseDispatcher
                <ClientMessage.ReadStreamEventsBackward, ClientMessage.ReadStreamEventsBackwardCompleted> readDispatcher,
            RequestResponseDispatcher<ClientMessage.WriteEvents, ClientMessage.WriteEventsCompleted> writeDispatcher,
            ILogger logger = null)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name == "") throw new ArgumentException("name");
            if (publisher == null) throw new ArgumentNullException("publisher");
            if (projectionStateHandler == null) throw new ArgumentNullException("projectionStateHandler");
            _projectionStateHandler = projectionStateHandler;
            _projectionCorrelationId = projectionCorrelationId;

            var namingBuilder = new ProjectionNamesBuilder();
            _projectionStateHandler.ConfigureSourceProcessingStrategy(namingBuilder);

            _name = namingBuilder.ForceProjectionName ?? name;
            _stateStreamNamePattern = namingBuilder.StateStreamName ?? ProjectionsStreamPrefix + name + "-{0}" + ProjectionsStateStreamSuffix;
            _stateStreamName = namingBuilder.StateStreamName ?? ProjectionsStreamPrefix + name + ProjectionsStateStreamSuffix; 
            _projectionConfig = projectionConfig;
            _logger = logger;
            _publisher = publisher;
            _readDispatcher = readDispatcher;
            _writeDispatcher = writeDispatcher;
            var builder = new CheckpointStrategy.Builder();
            _projectionStateHandler.ConfigureSourceProcessingStrategy(builder);
            _checkpointStrategy = builder.Build(_projectionConfig.Mode);
            _eventFilter = _checkpointStrategy.EventFilter;
            _partitionStateCache = new PartitionStateCache();
            _processingQueue = new CoreProjectionQueue(
                projectionCorrelationId, publisher, projectionConfig.PendingEventsThreshold, UpdateStatistics);
            _checkpointManager = this._checkpointStrategy.CreateCheckpointManager(
                this, projectionCorrelationId, this._publisher, this._readDispatcher,
                this._writeDispatcher, this._projectionConfig, this._name, _stateStreamName);
            GoToState(State.Initial);
        }

        internal void UpdateStatistics()
        {
            var info = new ProjectionStatistics();
            GetStatistics(info);
            _publisher.Publish(
                new ProjectionMessage.Projections.Management.StatisticsReport(_projectionCorrelationId, info));
        }

        public void Start()
        {
            EnsureState(State.Initial);
            GoToState(State.LoadStateRequsted);
        }

        public void Stop()
        {
            EnsureState(State.StateLoadedSubscribed | State.Paused | State.Resumed | State.Running);
            GoToState(State.Stopping);
        }

        public string GetProjectionState()
        {
            //TODO: separate requesting valid only state (not catching-up, non-stopped etc)
            //EnsureState(State.StateLoadedSubscribed | State.Stopping | State.Subscribed | State.Paused | State.Resumed | State.Running);
            return _partitionStateCache.GetLockedPartitionState("");
        }

        private void GetStatistics(ProjectionStatistics info)
        {
            _checkpointManager.GetStatistics(info);
            info.Status = _state.EnumVaueName() + info.Status + _processingQueue.GetStatus();
            info.Mode = _projectionConfig.Mode;
            info.Name = _name;
            info.StateReason = "";
            info.BufferedEvents = _processingQueue.GetBufferedEventCount();
            info.PartitionsCached = _partitionStateCache.CachedItemCount;
        }

        public void Handle(ProjectionMessage.SubscriptionMessage.CommittedEventReceived message)
        {
            if (IsOutOfOrderSubscriptionMessage(message))
                return;
            RegisterSubscriptionMessage(message);

            EnsureState(
                State.Running | State.Paused | State.Stopping | State.Stopped | State.FaultedStopping | State.Faulted);
            try
            {
                if (_state == State.Running || _state == State.Paused)
                {
                    CheckpointTag eventTag = message.CheckpointTag;
                    string partition = _checkpointStrategy.StatePartitionSelector.GetStatePartition(message);
                    var committedEventWorkItem = new CommittedEventWorkItem(this, message, partition);
                    _processingQueue.EnqueueTask(committedEventWorkItem, eventTag);
                }
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.SubscriptionMessage.ProgressChanged message)
        {
            if (IsOutOfOrderSubscriptionMessage(message))
                return;
            RegisterSubscriptionMessage(message);

            EnsureState(
                State.Running | State.Paused | State.Stopping | State.Stopped | State.FaultedStopping | State.Faulted);
            try
            {
                if (_state == State.Running || _state == State.Paused)
                {
                    var progressWorkItem = new ProgressWorkItem(this, _checkpointManager, message.Progress);
                    _processingQueue.EnqueueTask(progressWorkItem, message.CheckpointTag, allowCurrentPosition: true);
                }
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.SubscriptionMessage.CheckpointSuggested message)
        {
            if (IsOutOfOrderSubscriptionMessage(message))
                return;
            RegisterSubscriptionMessage(message);

            EnsureState(
                State.Running | State.Paused | State.Stopping | State.Stopped | State.FaultedStopping | State.Faulted);
            try
            {
                if ((_state == State.Running || _state == State.Paused) && _projectionConfig.CheckpointsEnabled)
                {
                    CheckpointTag checkpointTag = message.CheckpointTag;
                    var checkpointSuggestedWorkItem = new CheckpointSuggestedWorkItem(this, message, _checkpointManager);
                    _processingQueue.EnqueueTask(checkpointSuggestedWorkItem, checkpointTag);
                }
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.Projections.Management.GetState message)
        {
            EnsureState(
                State.Running | State.Paused | State.Stopping | State.Stopped | State.FaultedStopping | State.Faulted);
            try
            {
                if (_state == State.Running || _state == State.Paused)
                {
                    var getStateWorkItem = new GetStateWorkItem(message.Envelope, message.CorrelationId, this, _partitionStateCache, message.Partition);
                    _processingQueue.EnqueueOutOfOrderTask(getStateWorkItem);
                }
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.Projections.CheckpointCompleted message)
        {
            CheckpointCompleted(message.CheckpointTag);
        }

        public void Handle(ProjectionMessage.Projections.PauseRequested message)
        {
            Pause();
        }

        public void Handle(ProjectionMessage.Projections.CheckpointLoaded message)
        {
            EnsureState(State.LoadStateRequsted);
            try
            {
                OnLoadStateCompleted(message.CheckpointTag, message.CheckpointData);
                GoToState(State.StateLoadedSubscribed);
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        public void Handle(ProjectionMessage.Projections.RestartRequested message)
        {
            _logger.Info(
                "Projection '{0}'({1}) restart has been requested due to: '{2}'", _name, _projectionCorrelationId,
                message.Reason);
            //
            _publisher.Publish(new ProjectionMessage.Projections.UnsubscribeProjection(_projectionCorrelationId));
            GoToState(State.Initial);
            Start();
        }

        private void GoToState(State state)
        {
            var wasStopped = _state == State.Stopped || _state == State.Faulted;
            var wasStopping = _state == State.Stopping || _state == State.FaultedStopping;
            var wasStarted = _state == State.StateLoadedSubscribed || _state == State.Paused || _state == State.Resumed
                             || _state == State.Running || _state == State.Stopping || _state == State.FaultedStopping;
            _state = state; // set state before transition to allow further state change
            switch (state)
            {
                case State.Running:
                    _processingQueue.SetRunning();
                    break;
                case State.Paused:
                    _processingQueue.SetPaused();
                    break;
                default:
                    _processingQueue.SetStopped();
                    break;
            }
            switch (state)
            {
                case State.Stopped:
                case State.Faulted:
                    if (wasStarted && !wasStopped)
                        _checkpointManager.Stopped();
                    break;
                case State.Stopping:
                case State.FaultedStopping:
                    if (wasStarted && !wasStopping)
                        _checkpointManager.Stopping();
                    break;
            }
            switch (state)
            {
                case State.Initial:
                    EnterInitial();
                    break;
                case State.LoadStateRequsted:
                    EnterLoadStateRequested();
                    break;
                case State.StateLoadedSubscribed:
                    EnterStateLoadedSubscribed();
                    break;
                case State.Running:
                    EnterRunning();
                    break;
                case State.Paused:
                    EnterPaused();
                    break;
                case State.Resumed:
                    EnterResumed();
                    break;
                case State.Stopping:
                    EnterStopping();
                    break;
                case State.Stopped:
                    EnterStopped();
                    break;
                case State.FaultedStopping:
                    EnterFaultedStopping();
                    break;
                case State.Faulted:
                    EnterFaulted();
                    break;
                default:
                    throw new Exception();
            }
        }

        private void EnterInitial()
        {
            _handlerPartition = null;
            foreach (var requestId in _loadStateRequests)
                _readDispatcher.Cancel(requestId);
            _loadStateRequests.Clear();
            _partitionStateCache.Initialize();
            _processingQueue.Initialize();
            _checkpointManager.Initialize();
            _tickPending = false;
            _partitionStateCache.CacheAndLockPartitionState("", "", null);
            _expectedSubscriptionMessageSequenceNumber = -1; // this is to be overridden when subscribing
            // NOTE: this is to workaround exception in GetState requests submitted by client
        }

        private void EnterLoadStateRequested()
        {
            _checkpointManager.BeginLoadState();
        }

        private void EnterStateLoadedSubscribed()
        {
            GoToState(State.Running);
        }

        private void EnterRunning()
        {
            try
            {
                UpdateStatistics();
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        private void EnterPaused()
        {
        }

        private void EnterResumed()
        {
            GoToState(State.Running);
        }

        private void EnterStopping()
        {
            Console.WriteLine("Stopping");
            _publisher.Publish(new ProjectionMessage.Projections.UnsubscribeProjection(_projectionCorrelationId));
            // core projection may be stopped to change its configuration
            // it is important to checkpoint it so no writes pending remain when stopped
            _checkpointManager.RequestCheckpointToStop(); // should always report completed even if skipped
        }

        private void EnterStopped()
        {
            _publisher.Publish(new ProjectionMessage.Projections.StatusReport.Stopped(_projectionCorrelationId));
        }

        private void EnterFaultedStopping()
        {
            _publisher.Publish(new ProjectionMessage.Projections.UnsubscribeProjection(_projectionCorrelationId));
            // checkpoint last known correct state on fault
            _checkpointManager.RequestCheckpointToStop(); // should always report completed even if skipped
        }

        private void EnterFaulted()
        {
            _publisher.Publish(
                new ProjectionMessage.Projections.StatusReport.Faulted(_projectionCorrelationId, _faultedReason));
        }

        private bool IsOutOfOrderSubscriptionMessage(ProjectionMessage.SubscriptionMessage message)
        {
            return _expectedSubscriptionMessageSequenceNumber != message.SubscriptionMessageSequenceNumber;
        }

        private void RegisterSubscriptionMessage(ProjectionMessage.SubscriptionMessage message)
        {
            _expectedSubscriptionMessageSequenceNumber = message.SubscriptionMessageSequenceNumber + 1;
        }

        private void SetHandlerState(string partition)
        {
            if (_handlerPartition == partition)
                return;
            string newState = _partitionStateCache.GetLockedPartitionState(partition);
            _handlerPartition = partition;
            if (!string.IsNullOrEmpty(newState))
                _projectionStateHandler.Load(newState);
            else
                _projectionStateHandler.Initialize();
        }

        private void LoadProjectionStateFaulted(string newState, Exception ex)
        {
            _faultedReason =
                string.Format(
                    "Cannot load the {0} projection state.\r\nHandler: {1}\r\nState:\r\n\r\n{2}\r\n\r\nMessage:\r\n\r\n{3}",
                    _name, GetHandlerTypeName(), newState, ex.Message);
            if (_logger != null)
                _logger.ErrorException(ex, _faultedReason);
            GoToState(State.Faulted);
        }

        private string GetHandlerTypeName()
        {
            return _projectionStateHandler.GetType().Namespace + "." + _projectionStateHandler.GetType().Name;
        }

        private void TryResume()
        {
            GoToState(State.Resumed);
        }

        internal void ProcessCommittedEvent(
            CommittedEventWorkItem committedEventWorkItem, ProjectionMessage.SubscriptionMessage.CommittedEventReceived message,
            string partition)
        {

            EnsureState(State.Running);
            InternalProcessCommittedEvent(committedEventWorkItem, partition, message);
        }

        private void InternalProcessCommittedEvent(
            CommittedEventWorkItem committedEventWorkItem, string partition,
            ProjectionMessage.SubscriptionMessage.CommittedEventReceived message)
        {
            string newState = null;
            EmittedEvent[] emittedEvents = null;

            //TODO: not emitting (optimized) projection handlers can skip serializing state on each processed event
            bool hasBeenProcessed;
            try
            {
                hasBeenProcessed = ProcessEventByHandler(partition, message, out newState, out emittedEvents);
            }
            catch (Exception ex)
            {
                ProcessEventFaulted(
                    string.Format(
                        "The {0} projection failed to process an event.\r\nHandler: {1}\r\nEvent Position: {2}\r\n\r\nMessage:\r\n\r\n{3}",
                        _name, GetHandlerTypeName(), message.Position, ex.Message), ex);
                newState = null;
                emittedEvents = null;
                hasBeenProcessed = false;
            }
            newState = newState ?? "";
            if (hasBeenProcessed)
            {
                if (!ProcessEmittedEvents(committedEventWorkItem, emittedEvents))
                    return;

                if (_partitionStateCache.GetLockedPartitionState(partition) != newState)
                    // ensure state actually changed
                {
                    var lockPartitionStateAt = partition != "" ? message.CheckpointTag : null;
                    _partitionStateCache.CacheAndLockPartitionState(partition, newState, lockPartitionStateAt, message);
                    if (_projectionConfig.PublishStateUpdates)
                        EmitStateUpdated(committedEventWorkItem, partition, newState);
                }
            }
        }

        private bool ProcessEmittedEvents(CommittedEventWorkItem committedEventWorkItem, EmittedEvent[] emittedEvents)
        {
            if (_projectionConfig.EmitEventEnabled && _checkpointStrategy.IsEmiEnabled())
                EmitEmittedEvents(committedEventWorkItem, emittedEvents);
            else if (emittedEvents != null && emittedEvents.Length > 0)
            {
                ProcessEventFaulted("'emit' is not allowed by the projection/configuration/mode");
                return false;
            }
            return true;
        }

        private bool ProcessEventByHandler(
            string partition, ProjectionMessage.SubscriptionMessage.CommittedEventReceived message, out string newState,
            out EmittedEvent[] emittedEvents)
        {
            SetHandlerState(partition);
            return _projectionStateHandler.ProcessEvent(
                message.Position, message.EventStreamId, message.Data.EventType,
                _eventFilter.GetCategory(message.PositionStreamId), message.Data.EventId, message.EventSequenceNumber,
                Encoding.UTF8.GetString(message.Data.Metadata), Encoding.UTF8.GetString(message.Data.Data), out newState,
                out emittedEvents);
        }

        private void EmitEmittedEvents(CommittedEventWorkItem committedEventWorkItem, EmittedEvent[] emittedEvents)
        {
            bool result = emittedEvents != null && emittedEvents.Length > 0;
            if (result)
                committedEventWorkItem.ScheduleEmitEvents(emittedEvents);
        }

        private void EmitStateUpdated(CommittedEventWorkItem committedEventWorkItem, string partition, string newState)
        {
            committedEventWorkItem.ScheduleEmitEvents(
                new[]
                    {
                        new EmittedEvent(MakePartitionStateStreamName(partition), Guid.NewGuid(), "StateUpdated", newState)
                    });
        }

        private void ProcessEventFaulted(string faultedReason, Exception ex = null)
        {
            _faultedReason = faultedReason;
            if (_logger != null)
            {
                if (ex != null)
                    _logger.ErrorException(ex, _faultedReason);
                else
                    _logger.Error(_faultedReason);
            }
            GoToState(State.FaultedStopping);
        }

        private void EnsureState(State expectedStates)
        {
            if ((_state & expectedStates) == 0)
            {
                throw new Exception(
                    string.Format("Current state is {0}. Expected states are: {1}", _state, expectedStates));
            }
        }

        private void Tick()
        {
            // ignore any ticks received when not pending. this may happen when restart requested
            if (!_tickPending)
                return;
            EnsureState(State.Running | State.Paused | State.Stopping | State.FaultedStopping | State.Faulted);
            // we may get into faulted any time, so it is allowed
            try
            {
                _tickPending = false;
                _processingQueue.ProcessEvent();
            }
            catch (Exception ex)
            {
                SetFaulted(ex);
            }
        }

        private void OnLoadStateCompleted(CheckpointTag checkpointTag, string checkpointData)
        {
            if (checkpointTag == null)
            {
                var zeroTag = _checkpointStrategy.PositionTagger.MakeZeroCheckpointTag();
                InitializeProjectionFromCheckpoint("", zeroTag);
            }
            else
            {
                InitializeProjectionFromCheckpoint(checkpointData, checkpointTag);
            }
        }

        private void InitializeProjectionFromCheckpoint(string state, CheckpointTag checkpointTag)
        {
            EnsureState(State.Initial | State.LoadStateRequsted);
            //TODO: initialize projection state here (test it)
            //TODO: write test to ensure projection state is correctly loaded from a checkpoint and posted back when enough empty records processed
            _partitionStateCache.CacheAndLockPartitionState("", state, null);
            _checkpointManager.Start(checkpointTag);
            try
            {
                SetHandlerState("");
                GoToState(State.StateLoadedSubscribed);
            }
            catch (Exception ex)
            {
                LoadProjectionStateFaulted(state, ex);
                return;
            }
            _processingQueue.InitializeQueue(_checkpointStrategy.PositionTagger.MakeZeroCheckpointTag());
            _expectedSubscriptionMessageSequenceNumber = 0;
            _publisher.Publish(
                new ProjectionMessage.Projections.SubscribeProjection(
                    _projectionCorrelationId, this, checkpointTag, _checkpointStrategy,
                    _projectionConfig.CheckpointUnhandledBytesThreshold));
            _publisher.Publish(new ProjectionMessage.Projections.StatusReport.Started(_projectionCorrelationId));
        }

        internal void BeginStatePartitionLoad(string statePartition, CheckpointTag eventCheckpointTag, Action loadCompleted)
        {
            if (statePartition == "") // root is always cached
            {
                loadCompleted();
                return;
            }
            string state = _partitionStateCache.TryGetAndLockPartitionState(statePartition, eventCheckpointTag);
            if (state != null)
                loadCompleted();
            else
            {
                string partitionStateStreamName = MakePartitionStateStreamName(statePartition);
                _readRequestsInProgress++;
                _loadStateRequests.Add(_readDispatcher.Publish(
                    new ClientMessage.ReadStreamEventsBackward(
                        Guid.NewGuid(), _readDispatcher.Envelope, partitionStateStreamName, -1, 1, resolveLinks: false),
                    m => OnLoadStatePartitionCompleted(statePartition, m, loadCompleted, eventCheckpointTag)));
            }
        }

        private string MakePartitionStateStreamName(string statePartition)
        {
            return string.IsNullOrEmpty(statePartition)
                       ? _stateStreamName
                       : string.Format(_stateStreamNamePattern, statePartition);
        }

        private void OnLoadStatePartitionCompleted(
            string partition,
            ClientMessage.ReadStreamEventsBackwardCompleted message, Action loadCompleted, CheckpointTag eventCheckpointTag)
        {
            if (!_loadStateRequests.Remove(message.CorrelationId))
                _logger.Info("strange");
            _readRequestsInProgress--;
            if (message.Events.Length == 1)
            {
                EventRecord @event = message.Events[0].Event;
                if (@event.EventType == "StateUpdated")
                {
                    var checkpointTag = @event.Metadata.ParseJson<CheckpointTag>();
                    // always recovery mode? skip until state before current event
                    //TODO: skip event processing in case we know i has been already processed
                    CheckpointTag eventPositionTag = eventCheckpointTag;
                    if (checkpointTag < eventPositionTag)
                    {
                        _partitionStateCache.CacheAndLockPartitionState(
                            partition, Encoding.UTF8.GetString(@event.Data), eventPositionTag, this);
                        loadCompleted();
                        EnsureTickPending();
                        return;
                    }
                }
            }
            if (message.NextEventNumber == -1)
            {
                _partitionStateCache.CacheAndLockPartitionState(partition, "", eventCheckpointTag, this);
                loadCompleted();
                EnsureTickPending();
                return;
            }
            string partitionStateStreamName = MakePartitionStateStreamName(partition);
            _readRequestsInProgress++;
            _loadStateRequests.Add(_readDispatcher.Publish(
                new ClientMessage.ReadStreamEventsBackward(
                    Guid.NewGuid(), _readDispatcher.Envelope, partitionStateStreamName, message.NextEventNumber, 1,
                    resolveLinks: false),
                m => OnLoadStatePartitionCompleted(partition, m, loadCompleted, eventCheckpointTag)));
        }

        public void Dispose()
        {
            if (_projectionStateHandler != null)
                _projectionStateHandler.Dispose();
        }

        internal void EnsureTickPending()
        {
            if (_tickPending)
                return;
            if (_state == State.Running || _state == State.Stopping || _state == State.FaultedStopping)
            {
                _tickPending = true;
                _publisher.Publish(new ProjectionMessage.CoreService.Tick(Tick));
            }
        }

        private void SetFaulted(Exception ex)
        {
            _faultedReason = ex.Message;
            GoToState(State.Faulted);
        }

        private void CheckpointCompleted(CheckpointTag lastCompletedCheckpointPosition)
        {
            // all emitted events caused by events before the checkpoint position have been written  
            // unlock states, so the cache can be clean up as they can now be safely reloaded from the ES
            _partitionStateCache.Unlock(lastCompletedCheckpointPosition);

            switch (_state)
            {
                case State.Paused:
                    TryResume();
                    break;
                case State.Stopping:
                    GoToState(State.Stopped);
                    break;
                case State.FaultedStopping:
                    GoToState(State.Faulted);
                    break;
            }
        }

        private void Pause()
        {
            if (_state != State.Stopping && _state != State.FaultedStopping)
                // stopping projection is already paused
                GoToState(State.Paused);
        }

        internal void FinalizeEventProcessing(
            List<EmittedEvent[]> scheduledWrites, CheckpointTag eventCheckpointTag, float progress)
        {
            if (_state != State.Faulted && _state != State.FaultedStopping)
            {
                EnsureState(State.Running);
                //TODO: move to separate projection method and cache result in work item
                var checkpointTag = eventCheckpointTag;
                _checkpointManager.EventProcessed(
                    GetProjectionState(), scheduledWrites, checkpointTag, progress);
            }
        }
    }
}
