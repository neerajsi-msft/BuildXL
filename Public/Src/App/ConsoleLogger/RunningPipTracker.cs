// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Linq;
using BuildXL.Pips;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Tracing;
using BuildXL.ViewModel;

namespace BuildXL
{
    internal sealed class RunningPipTracker
    {
        private readonly Dictionary<PipId, PipInfo> m_runningPips = new Dictionary<PipId, PipInfo>();

        private sealed class PipInfo
        {
            public string Description;
            public DateTime FirstSeen;
            public DateTime LastSeen;
        }

        public string GetSnapshot(BuildViewModel viewModel, int maxStatusPips, DateTime now)
        {
            Contract.RequiresNotNull(viewModel.Context);
            Contract.Requires(maxStatusPips > 0);

            lock (m_runningPips)
            {
                foreach (var pip in viewModel.RetrieveExecutingProcessPips())
                {
                    if (!m_runningPips.TryGetValue(pip.PipId, out var info))
                    {
                        info = new PipInfo
                        {
                            Description = pip.HydratePip().GetShortDescription(viewModel.Context),
                            FirstSeen = now,
                        };
                        m_runningPips.Add(pip.PipId, info);
                    }

                    info.LastSeen = now;
                }

                using (var pooled = Pools.StringBuilderPool.GetInstance())
                {
                    var builder = pooled.Instance;
                    int count = 0;
                    foreach (var item in m_runningPips.ToArray().OrderBy(kvp => kvp.Value.FirstSeen))
                    {
                        if (item.Value.LastSeen < now)
                        {
                            m_runningPips.Remove(item.Key);
                        }
                        else if (++count <= maxStatusPips)
                        {
                            if (builder.Length > 0)
                            {
                                builder.AppendLine();
                            }

                            builder.AppendFormat(
                                CultureInfo.InvariantCulture,
                                "   {0} {1}",
                                FormattingEventListener.TimeSpanToString(TimeDisplay.Seconds, now - item.Value.FirstSeen),
                                item.Value.Description);
                        }
                    }

                    if (count > maxStatusPips)
                    {
                        builder.AppendLine();
                        builder.AppendFormat(ConsoleLogger.Strings.ConsoleListener_AdditionalPips, count - maxStatusPips);
                    }

                    return builder.ToString();
                }
            }
        }
    }
}
