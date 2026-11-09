// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BuildXL.FrontEnd.DirectGraph
{
    internal sealed class DirectGraphFramedReader : TextReader
    {
        private readonly TextReader m_reader;
        private readonly char[] m_buffer = new char[8192];
        private int m_position;
        private int m_count;
        public bool LastLineHadLf { get; private set; }

        public DirectGraphFramedReader(TextReader reader) => m_reader = reader;

        public override string ReadLine()
        {
            StringBuilder joined = null;
            LastLineHadLf = false;
            while (true)
            {
                if (m_position == m_count)
                {
                    m_count = m_reader.Read(m_buffer, 0, m_buffer.Length);
                    m_position = 0;
                    if (m_count == 0) return joined?.ToString();
                }
                int end = Array.IndexOf(m_buffer, '\n', m_position, m_count - m_position);
                int length = (end < 0 ? m_count : end) - m_position;
                if (end >= 0)
                {
                    string line;
                    if (joined == null)
                    {
                        line = new string(m_buffer, m_position, length);
                    }
                    else
                    {
                        joined.Append(m_buffer, m_position, length);
                        line = joined.ToString();
                    }
                    m_position = end + 1;
                    LastLineHadLf = true;
                    return line;
                }
                joined ??= new StringBuilder();
                joined.Append(m_buffer, m_position, length);
                m_position = m_count;
            }
        }
    }

    internal sealed class DirectGraphTabReader
    {
        private readonly DirectGraphFramedReader m_reader;
        private readonly string m_file;
        private readonly List<string> m_paths = new();
        private readonly List<string> m_strings = new();
        private readonly List<string[]> m_sets = new();
        private string m_current;
        private int m_line = 1;

        public DirectGraphTabReader(DirectGraphFramedReader reader, string file)
        {
            m_reader = reader;
            m_file = file;
        }

        public IEnumerable<DirectGraphTextRecord> Read()
        {
            Advance();
            while (m_current != null)
            {
                string[] header = Tokens(0, registration: true);
                string kind = header[0];
                if (kind is "path" or "string")
                {
                    var table = kind == "path" ? m_paths : m_strings;
                    if (header.Length != 3 || !DirectGraphTextReader.TryId(header[1], out int id) || id != table.Count)
                        throw Error($"A {kind} registration requires the next dense ID ({table.Count}) and a raw payload.");
                    table.Add(Literal(header[2], 0));
                    continue;
                }
                if (header.Length != 2 || !Bare(header[1]) || header[1][0] is '$' or '@')
                    throw Error("Expected a record kind and bare name.");
                if (kind == "set")
                {
                    if (!DirectGraphTextReader.TryId(header[1], out int id) || id != m_sets.Count)
                        throw Error($"A dependency set requires the next dense ID ({m_sets.Count}).");
                    Advance();
                    var members = new List<string>();
                    while (Depth() > 0)
                    {
                        var child = Tokens(1);
                        if (child.Length != 1 || child[0].Length < 2 || child[0][0] != 'p')
                            throw Error("Dependency-set children must be pID references.");
                        members.Add(Reference(child[0].Substring(1), 'p'));
                        Advance();
                    }
                    m_sets.Add(members.ToArray());
                    continue;
                }
                if (kind is not ("process" or "writeFile" or "copyFile" or "sealDirectory" or "composeSharedOpaque" or "export"))
                    throw Error($"Unknown operation '{kind}'.");
                if (ReservedName(header[1]))
                    throw Error($"Record name '{header[1]}' is reserved for sID/pID references; choose a different name.");
                var record = new DirectGraphTextRecord(kind, header[1], $"{m_file}:{m_line}");
                Advance();
                while (Depth() > 0)
                {
                    var field = Tokens(1);
                    string name = field[0];
                    if (name == "argument") throw Error("DirectGraph 3 uses one arguments expression.");
                    bool data = name is "arguments" or "environment" ||
                        (name == "content" && kind == "writeFile") || (name == "description" && kind == "process");
                    if (data)
                    {
                        int offset = name == "environment" ? 2 : 1;
                        if (field.Length <= offset) throw Error($"Field '{name}' requires an expression on the same line.");
                        string[] parameters = name == "environment" ? new[] { ReferenceToken(field[1], 's') } : Array.Empty<string>();
                        var expression = Expression(1, field.Skip(offset).ToArray());
                        if (!record.PipDataFields.TryGetValue(name, out var rows))
                            record.PipDataFields.Add(name, rows = new List<DirectGraphTextDataField>());
                        rows.Add(new DirectGraphTextDataField(parameters, expression));
                    }
                    else
                    {
                        if (field.Length < 2) throw Error("A field requires a value.");
                        var values = new List<string>();
                        for (int i = 1; i < field.Length; i++)
                        {
                            string token = field[i];
                            if (token.StartsWith("@", StringComparison.Ordinal))
                            {
                                if (name is not ("input" or "orderOnly") || !DirectGraphTextReader.TryId(token.AsSpan(1), out int id) || id >= m_sets.Count)
                                    throw Error("Unknown or misplaced dependency-set reference.");
                                values.AddRange(m_sets[id]);
                            }
                            else
                            {
                                char type = DirectGraphTextReader.ValueKind(kind, name, i - 1);
                                if (type != '\0') values.Add(ReferenceToken(token, type));
                                else
                                {
                                    if (token.StartsWith("$", StringComparison.Ordinal)) throw Error("Dollar-prefixed references are not supported in DirectGraph 3.");
                                    string value = token.Length > 1 && token[0] == 's' && DirectGraphTextReader.TryId(token.AsSpan(1), out _) ?
                                        ReferenceToken(token, 's') : token;
                                    if ((name == "option" || (i > 1 && name is "inputDirectory" or "directory")) &&
                                        ReservedName(value))
                                        throw Error($"Name '{value}' in field '{name}' is reserved for sID/pID references; choose a different name.");
                                    values.Add(value);
                                }
                            }
                        }
                        record.Add(name, values.ToArray());
                        Advance();
                    }
                }
                yield return record;
            }
        }

        private DirectGraphTextExpression Expression(int depth, string[] tokens)
        {
            if (depth > 128) throw Error("PipData nesting exceeds 128 levels.");
            string kind = tokens[0];
            if (tokens.Length == 1 && kind.Length >= 2 && kind[0] is 's' or 'p')
            {
                var leaf = Leaf(kind);
                Advance();
                return leaf;
            }
            if (kind != "concat")
                throw Error($"Unknown or malformed PipData operator '{kind}'.");

            int marker = Array.FindIndex(tokens, 1, token => token is ":" or "-");
            if (marker < 0) throw Error("A concat requires ':' for block children or '-' for inline references.");
            int option = 1;
            string value = string.Empty;
            if (option < marker && tokens[option] != "escapeArguments")
            {
                var separator = Leaf(tokens[option++]);
                if (separator.Kind != "stringRef") throw Error("A concat separator must be an sID reference.");
                value = separator.Value;
            }
            bool escapeArguments = option < marker && tokens[option] == "escapeArguments";
            if (escapeArguments) option++;
            if (option != marker) throw Error("Unknown concat header option.");
            bool inline = tokens[marker] == "-";
            if (!inline && marker != tokens.Length - 1) throw Error("Block concat children must start on the next line.");
            var children = new List<DirectGraphTextExpression>();
            if (inline)
            {
                for (int i = marker + 1; i < tokens.Length; i++) children.Add(Leaf(tokens[i]));
                Advance();
                if (Depth() > depth) throw Error("An inline concat cannot have block children.");
            }
            else
            {
                Advance();
                while (Depth() > depth) children.Add(Expression(depth + 1, Tokens(depth + 1)));
            }
            return new DirectGraphTextExpression(kind, value, children, escapeArguments);
        }

        private DirectGraphTextExpression Leaf(string token)
        {
            if (token.Length < 2 || token[0] is not ('s' or 'p')) throw Error("Expected an sID or pID reference.");
            return new DirectGraphTextExpression(token[0] == 's' ? "stringRef" : "pathRef",
                Reference(token.Substring(1), token[0]), Array.Empty<DirectGraphTextExpression>());
        }

        private string Literal(string first, int depth)
        {
            if (first.Contains('\0')) throw Error("NUL is not allowed.");
            Advance();
            StringBuilder value = null;
            while (Depth() > depth)
            {
                string part = m_current.Substring(depth + 1);
                if (part.Contains('\0')) throw Error("NUL is not allowed.");
                value ??= new StringBuilder(first);
                value.Append('\n').Append(part);
                Advance();
            }
            return value?.ToString() ?? first;
        }

        private string[] Tokens(int depth, bool registration = false)
        {
            if (m_current == null || Depth() != depth) throw Error($"Expected exactly {depth} leading TABs.");
            string line = m_current.Substring(depth);
            int first = line.IndexOf(' ');
            if (registration && first >= 0)
            {
                string tag = line.Substring(0, first);
                if (tag is "path" or "string")
                {
                    int second = line.IndexOf(' ', first + 1);
                    if (second < 0) throw Error("A registration requires an ID and payload separator.");
                    return new[] { tag, line.Substring(first + 1, second - first - 1), line.Substring(second + 1) };
                }
            }
            var tokens = line.Split(' ');
            if (tokens.Any(token => !Bare(token))) throw Error("Structural tokens require single spaces and no quotes, escapes, or control characters.");
            return tokens;
        }

        private static bool Bare(string token) => token.Length > 0 && !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\\');

        private static bool ReservedName(string name)
        {
            if (name.Length < 2 || name[0] is not ('s' or 'p')) return false;
            for (int i = 1; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return true;
        }

        private string ReferenceToken(string token, char type)
        {
            if (token.Length < 2 || token[0] != type) throw Error($"Expected {type}ID.");
            return Reference(token.Substring(1), type);
        }

        private string Reference(string token, char type)
        {
            var table = type == 'p' ? m_paths : m_strings;
            if (!DirectGraphTextReader.TryId(token, out int id) || id >= table.Count) throw Error($"Unknown {type} reference '{token}'.");
            return table[id];
        }

        private int Depth()
        {
            int depth = 0;
            if (m_current != null) while (depth < m_current.Length && m_current[depth] == '\t') depth++;
            return depth;
        }

        private void Advance()
        {
            try
            {
                m_current = m_reader.ReadLine();
                if (m_current != null)
                {
                    m_line++;
                    if (!m_reader.LastLineHadLf) throw Error("DirectGraph 3 requires a final framing LF.");
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"{m_file}:{m_line}: Invalid UTF-8.", exception);
            }
        }

        private InvalidDataException Error(string message) => new($"{m_file}:{m_line}: {message}");
    }
}
