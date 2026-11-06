// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.IO;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tracing;
using BuildXL.ViewModel;
using LogEventId = BuildXL.Scheduler.Tracing.LogEventId;

namespace BuildXL
{
    /// <summary>
    /// Appends plain-text running-pip snapshots to file scheduler status updates.
    /// </summary>
    public sealed class RunningPipsEventListener : TextWriterEventListener
    {
        private readonly BuildViewModel m_buildViewModel;
        private readonly int m_maxStatusPips;
        private readonly RunningPipTracker m_runningPips;

        /// <nodoc />
        public RunningPipsEventListener(
            Events eventSource,
            TextWriter writer,
            DateTime baseTime,
            BuildViewModel buildViewModel,
            int maxStatusPips,
            WarningMapper warningMapper = null,
            EventLevel level = EventLevel.Verbose,
            TimeDisplay timeDisplay = TimeDisplay.None,
            EventMask eventMask = null,
            DisabledDueToDiskWriteFailureEventHandler onDisabledDueToDiskWriteFailure = null,
            PathTranslator pathTranslator = null)
            : base(eventSource, writer, baseTime, warningMapper, level, timeDisplay, eventMask,
                  onDisabledDueToDiskWriteFailure, pathTranslator)
        {
            Contract.RequiresNotNull(buildViewModel);
            Contract.Requires(maxStatusPips >= 0);

            m_buildViewModel = buildViewModel;
            m_maxStatusPips = maxStatusPips;
            m_runningPips = maxStatusPips > 0 ? new RunningPipTracker() : null;
        }

        /// <inheritdoc />
        protected override void Output(EventLevel level, EventWrittenEventArgs eventData, string text, bool doNotTranslatePaths = false)
        {
            if (m_runningPips != null && m_buildViewModel.Context != null &&
                (eventData.EventId == (int)SharedLogEventId.PipStatus ||
                 eventData.EventId == (int)LogEventId.PipStatusNonOverwriteable))
            {
                string snapshot = m_runningPips.GetSnapshot(m_buildViewModel, m_maxStatusPips, DateTime.UtcNow);
                if (snapshot.Length > 0)
                {
                    text += Environment.NewLine + "Oldest running process pips:" + Environment.NewLine + snapshot;
                }
            }

            base.Output(level, eventData, text, doNotTranslatePaths);
        }
    }
}
