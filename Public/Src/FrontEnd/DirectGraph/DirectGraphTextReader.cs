// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BuildXL.FrontEnd.DirectGraph
{
    /// <summary>
    /// Reads one operation at a time. Dense path, string, and dependency-set IDs are scoped to this file.
    /// </summary>
    public sealed class DirectGraphTextReader
    {
        private readonly TextReader m_reader;
        private readonly string m_file;
        private readonly List<string> m_paths = new();
        private readonly List<string> m_strings = new();
        private readonly List<string[]> m_sets = new();
        private int m_line;

        /// <nodoc />
        public DirectGraphTextReader(TextReader reader, string file)
        {
            m_reader = reader;
            m_file = file;
        }

        /// <summary>Enumerates records without retaining previously read operations.</summary>
        public IEnumerable<DirectGraphTextRecord> Read()
        {
            bool header = false;
            DirectGraphTextRecord current = null;
            string line;
            while ((line = m_reader.ReadLine()) != null)
            {
                m_line++;
                if (string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var tokens = Tokenize(line);
                bool indented = char.IsWhiteSpace(line[0]);
                if (!header)
                {
                    if (indented || tokens.Count != 2 || tokens[0].Span is not "directgraph" || tokens[1].Span is not "2" ||
                        tokens.Any(token => token.Quoted))
                    {
                        throw Error("Expected 'directgraph 2' header.");
                    }
                    header = true;
                    continue;
                }

                if (indented)
                {
                    if (current == null || tokens[0].Quoted || tokens.Count < 2)
                    {
                        throw Error("Expected an operation field and its value.");
                    }
                    string field = tokens[0].Value.ToString();
                    var values = new List<string>();
                    for (int index = 1; index < tokens.Count; index++)
                    {
                        var token = tokens[index];
                        char valueKind = ValueKind(current.Kind, field, index - 1);
                        if (!token.Quoted && token.Span.StartsWith("@", StringComparison.Ordinal))
                        {
                            if (field is not ("input" or "orderOnly") ||
                                !TryId(token.Span.Slice(1), out int id) || id >= m_sets.Count)
                            {
                                throw Error($"Unknown or misplaced dependency set '{token.Value}'.");
                            }
                            values.AddRange(m_sets[id]);
                        }
                        else
                        {
                            values.Add(ResolveValue(token, valueKind));
                        }
                    }
                    current.Add(field, values.ToArray());
                    continue;
                }

                if (current != null)
                {
                    yield return current;
                    current = null;
                }
                if (tokens[0].Quoted || tokens.Count < 2)
                {
                    throw Error("Expected a record kind and name.");
                }
                if (tokens[1].Span.IsWhiteSpace() || tokens[1].Quoted || tokens[1].Span[0] is '$' or '@')
                {
                    throw Error("Record names must be nonempty bare identifiers.");
                }
                if (tokens[0].Span is "path" or "string")
                {
                    var table = tokens[0].Span is "path" ? m_paths : m_strings;
                    if (!TryId(tokens[1].Span, out int id) || id != table.Count || tokens.Count != 3 || !tokens[2].Quoted)
                    {
                        throw Error($"A {tokens[0].Value} registration must use the next dense ID ({table.Count}) and one quoted literal.");
                    }
                    table.Add(tokens[2].Value.ToString());
                }
                else if (tokens[0].Span is "set")
                {
                    if (!TryId(tokens[1].Span, out int id) || id != m_sets.Count)
                    {
                        throw Error($"A dependency set must use the next dense ID ({m_sets.Count}).");
                    }
                    m_sets.Add(tokens.Skip(2).Select(token => ResolveValue(token, 'p')).ToArray());
                }
                else
                {
                    string kind = tokens[0].Value.ToString();
                    if (tokens.Count != 2 || kind is not ("process" or "writeFile" or "copyFile" or "sealDirectory" or "composeSharedOpaque" or "export"))
                    {
                        throw Error($"Unknown or malformed operation '{kind}'.");
                    }
                    current = new DirectGraphTextRecord(kind, tokens[1].Value.ToString(), $"{m_file}:{m_line}");
                }
            }
            if (!header)
            {
                throw Error("Missing 'directgraph 2' header.");
            }
            if (current != null)
            {
                yield return current;
            }
        }

        private static bool TryId(ReadOnlySpan<char> value, out int id) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            (value.Length == 1 || value[0] != '0');

        private static char ValueKind(string operation, string field, int index) => field switch
        {
            "destination" or "source" or "executable" or "workingDirectory" or "input" or "orderOnly" or
                "output" or "rewrite" or "file" or "directoryRoot" => 'p',
            "inputDirectory" or "outputDirectory" or "directory" => index == 0 ? 'p' : '\0',
            "content" => operation == "sealDirectory" ? 'p' : 's',
            "argument" or "environment" or "passthroughEnvironment" or "description" or "pattern" => 's',
            _ => '\0',
        };

        private string ResolveValue(Token token, char kind)
        {
            if (kind == '\0')
            {
                if (!token.Quoted && token.Span.StartsWith("$", StringComparison.Ordinal))
                {
                    throw Error($"This field requires a literal, not an intern reference '{token.Value}'.");
                }
                return token.Value.ToString();
            }
            var table = kind == 'p' ? m_paths : m_strings;
            if (token.Quoted || token.Value.Length < 2 || token.Span[0] != '$' || token.Span[1] != kind ||
                !TryId(token.Span.Slice(2), out int id) || id >= table.Count)
            {
                throw Error($"Expected a registered {(kind == 'p' ? "path" : "string")} reference (${kind}ID), got '{token.Value}'.");
            }
            return table[id];
        }

        private List<Token> Tokenize(string line)
        {
            var result = new List<Token>();
            int i = 0;
            while (i < line.Length)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    i++;
                    continue;
                }
                bool quoted = line[i] == '"';
                int start = i;
                ReadOnlyMemory<char> value;
                if (quoted)
                {
                    start = ++i;
                    StringBuilder decoded = null;
                    while (i < line.Length && line[i] != '"')
                    {
                        char c = line[i++];
                        if (c == '\\')
                        {
                            decoded ??= new StringBuilder().Append(line, start, i - start - 1);
                            if (i == line.Length)
                            {
                                throw Error("Incomplete escape.");
                            }
                            char escape = line[i++];
                            c = escape switch
                            {
                                '"' => '"',
                                '\\' => '\\',
                                'n' => '\n',
                                'r' => '\r',
                                't' => '\t',
                                'u' => ReadUnicode(line, ref i),
                                _ => throw Error($"Unknown escape '\\{escape}'."),
                            };
                        }
                        else if (char.IsControl(c))
                        {
                            throw Error("Control characters must be escaped.");
                        }
                        if (c == '\0')
                        {
                            throw Error("NUL is not allowed.");
                        }
                        decoded?.Append(c);
                    }
                    if (i == line.Length || (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1])))
                    {
                        throw Error("Unterminated quoted value or missing token separator.");
                    }
                    value = decoded == null ? line.AsMemory(start, i - start) : decoded.ToString().AsMemory();
                    i++;
                }
                else
                {
                    while (i < line.Length && !char.IsWhiteSpace(line[i]))
                    {
                        if (line[i] is '"' or '\\' || char.IsControl(line[i]))
                        {
                            throw Error("Quote values containing quotes, escapes, or control characters.");
                        }
                        i++;
                    }
                    value = line.AsMemory(start, i - start);
                }
                result.Add(new Token(value, quoted));
            }
            return result;
        }

        private char ReadUnicode(string line, ref int index)
        {
            if (index + 4 > line.Length ||
                !ushort.TryParse(line.AsSpan(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
            {
                throw Error("Expected four hexadecimal digits after '\\u'.");
            }
            index += 4;
            return (char)code;
        }

        private InvalidDataException Error(string message) => new($"{m_file}:{m_line}: {message}");

        private readonly struct Token
        {
            public ReadOnlyMemory<char> Value { get; }
            public ReadOnlySpan<char> Span => Value.Span;
            public bool Quoted { get; }

            public Token(ReadOnlyMemory<char> value, bool quoted)
            {
                Value = value;
                Quoted = quoted;
            }
        }
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

        internal DirectGraphTextRecord(string kind, string name, string location)
        {
            Kind = kind;
            Name = name;
            Location = location;
        }

        internal void Add(string name, string[] values)
        {
            if (!Fields.TryGetValue(name, out var fields))
            {
                Fields.Add(name, fields = new List<string[]>());
            }
            fields.Add(values);
        }
    }
}