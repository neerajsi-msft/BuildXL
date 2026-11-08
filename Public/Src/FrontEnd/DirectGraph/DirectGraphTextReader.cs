// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BuildXL.FrontEnd.DirectGraph
{
    /// <summary>Reads compact v3 operations with fragment-local dense IDs.</summary>
    public sealed class DirectGraphTextReader
    {
        private readonly DirectGraphFramedReader m_reader;
        private readonly string m_file;

        /// <nodoc />
        public DirectGraphTextReader(TextReader reader, string file)
        {
            m_reader = new DirectGraphFramedReader(reader);
            m_file = file;
        }

        /// <summary>Enumerates records without retaining previously read operations.</summary>
        public IEnumerable<DirectGraphTextRecord> Read()
        {
            string header;
            try
            {
                header = m_reader.ReadLine();
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"{m_file}:1: Invalid UTF-8 in DirectGraph fragment.", exception);
            }
            if (header != "directgraph 3" || !m_reader.LastLineHadLf)
                throw new InvalidDataException($"{m_file}:1: Expected 'directgraph 3' followed by LF.");
            return new DirectGraphTabReader(m_reader, m_file).Read();
        }

        internal static bool TryId(ReadOnlySpan<char> value, out int id) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            (value.Length == 1 || value[0] != '0');

        internal static char ValueKind(string operation, string field, int index) => field switch
        {
            "destination" or "source" or "executable" or "workingDirectory" or "input" or "orderOnly" or
                "output" or "rewrite" or "file" or "directoryRoot" or "tempDirectory" => 'p',
            "inputDirectory" or "outputDirectory" or "directory" => index == 0 ? 'p' : '\0',
            "content" => operation == "sealDirectory" ? 'p' : 's',
            "passthroughEnvironment" or "description" or "pattern" or "sourceExclusion" => 's',
            "semaphore" => index == 0 ? 's' : '\0',
            _ => '\0',
        };
    }

    /// <summary>A single operation, bounded by the next top-level record or end of file.</summary>
    public sealed class DirectGraphTextRecord
    {
        /// <nodoc />
        public string Kind { get; }
        /// <nodoc />
        public string Name { get; }
        /// <nodoc />
        public string Location { get; }
        /// <nodoc />
        public Dictionary<string, List<string[]>> Fields { get; } = new(StringComparer.Ordinal);
        /// <summary>Structured, typed PipData fields.</summary>
        public Dictionary<string, List<DirectGraphTextDataField>> PipDataFields { get; } = new(StringComparer.Ordinal);

        internal DirectGraphTextRecord(string kind, string name, string location)
        {
            Kind = kind;
            Name = name;
            Location = location;
        }

        internal void Add(string name, string[] values)
        {
            if (!Fields.TryGetValue(name, out var fields))
                Fields.Add(name, fields = new List<string[]>());
            fields.Add(values);
        }
    }

    /// <summary>A field whose value is a PipData expression, with optional field parameters.</summary>
    public sealed record DirectGraphTextDataField(string[] Parameters, DirectGraphTextExpression Expression);

    /// <summary>A typed string/path reference or concatenation with optional whole-child argument escaping.</summary>
    public sealed record DirectGraphTextExpression(
        string Kind, string Value, IReadOnlyList<DirectGraphTextExpression> Children, bool EscapeArguments = false);
}
