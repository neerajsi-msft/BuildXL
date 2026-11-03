// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildXL.FrontEnd.DirectGraph;

namespace GraphRenderer
{
    /// <summary>
    /// Renders a fragment for inspection without executing commands or linking project artifacts.
    /// </summary>
    public static class Renderer
    {
        private const string Usage = "Usage: dg-render <fragment.dg> [--operation <name>] [--output <path-or-basename>]";
        private static readonly JsonSerializerOptions s_jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>Returns 0 on success, 1 for no matches, or 2 for invalid arguments, input, or I/O.</summary>
        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            try
            {
                string file = null, operation = null, outputPath = null;
                bool positionalOnly = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (!positionalOnly)
                    {
                        switch (args[i])
                        {
                            case "--help":
                            case "-h":
                                output.WriteLine(Usage);
                                output.WriteLine("Filters combine with AND. Paths match exactly or by basename; comparisons are case-sensitive.");
                                output.WriteLine("Output is a debug view, not DG input or an executable script. Multiline values show explicit line endings.");
                                return 0;
                            case "--":
                                positionalOnly = true;
                                continue;
                            case "--operation":
                                operation = ReadFilter(args, ref i, operation);
                                continue;
                            case "--output":
                                outputPath = ReadFilter(args, ref i, outputPath);
                                continue;
                        }
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"Unknown option '{args[i]}'.");
                        }
                    }
                    if (file != null)
                    {
                        throw new ArgumentException("Expected exactly one fragment file.");
                    }
                    file = args[i];
                }
                if (string.IsNullOrEmpty(file))
                {
                    throw new ArgumentException("A fragment file is required.");
                }
                using var reader = File.OpenText(file);
                if (Render(reader, file, output, operation, outputPath) == 0)
                {
                    error.WriteLine("No operations matched the requested filters.");
                    return 1;
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error.WriteLine(exception.Message);
                error.WriteLine(Usage);
                return 2;
            }
        }

        /// <summary>Streams operations, expanding references and returning the number rendered.</summary>
        public static int Render(TextReader reader, string file, TextWriter output, string operation = null, string outputPath = null)
        {
            output.WriteLine("# DirectGraph debug view; not DG input or an executable script.");
            int count = 0;
            foreach (var record in new DirectGraphTextReader(reader, file).Read())
            {
                if ((operation != null && record.Name != operation) || (outputPath != null && !MatchesOutput(record, outputPath)))
                {
                    continue;
                }
                output.WriteLine();
                output.WriteLine($"{record.Kind} {Quote(record.Name)}");
                WriteValue(output, "source", record.Location);
                foreach (var field in record.Fields)
                {
                    for (int i = 0; i < field.Value.Count; i++)
                    {
                        var row = field.Value[i];
                        string label = field.Key == "argument" || field.Value.Count > 1 ? $"{field.Key}[{i}]" : field.Key;
                        if (field.Key == "argument" || (record.Kind == "writeFile" && field.Key == "content"))
                        {
                            WriteValue(output, label, string.Concat(row));
                        }
                        else if (row.Length == 1)
                        {
                            WriteValue(output, label, row[0]);
                        }
                        else
                        {
                            output.WriteLine($"  {label}: {JsonSerializer.Serialize(row, s_jsonOptions)}");
                        }
                    }
                }
                count++;
            }
            return count;
        }

        private static string ReadFilter(string[] args, ref int index, string previous)
        {
            string option = args[index];
            if (previous != null || index + 1 == args.Length || string.IsNullOrEmpty(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires one value and may occur only once.");
            }
            return args[++index];
        }

        private static bool MatchesOutput(DirectGraphTextRecord record, string path)
        {
            string normalized = path.Replace('\\', '/');
            foreach (string field in new[] { "output", "rewrite", "destination", "outputDirectory", "directoryRoot" })
            {
                if (record.Fields.TryGetValue(field, out var rows) && rows.Any(row => row.Length > 0 &&
                    (row[0].Replace('\\', '/') == normalized ||
                    (!normalized.Contains('/') && row[0].Replace('\\', '/').Split('/').Last() == normalized))))
                {
                    return true;
                }
            }
            return false;
        }

        private static void WriteValue(TextWriter output, string label, string value)
        {
            if (!value.Contains('\r') && !value.Contains('\n'))
            {
                output.WriteLine($"  {label}: {Quote(value)}");
                return;
            }
            output.WriteLine($"  {label}: block ({value.Length} chars; explicit line endings)");
            int start = 0;
            while (start < value.Length)
            {
                int end = start;
                while (end < value.Length && value[end] is not ('\r' or '\n'))
                {
                    end++;
                }
                string ending = "EOF";
                int next = end;
                if (end < value.Length)
                {
                    bool crlf = value[end] == '\r' && end + 1 < value.Length && value[end + 1] == '\n';
                    ending = crlf ? "CRLF" : value[end] == '\r' ? "CR" : "LF";
                    next += crlf ? 2 : 1;
                }
                output.WriteLine($"    {Quote(value.Substring(start, end - start))} + {ending}");
                start = next;
            }
        }

        private static string Quote(string value) => JsonSerializer.Serialize(value, s_jsonOptions);
    }
}