using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Report;

internal sealed partial class ReportStyleSheet
{
    // ---------------------------------------------------------------- reading a sheet

    /// <summary>
    /// Reads style sheet text and adds its rules after the ones already read (so it wins ties). Problems are logged and
    /// skipped. <paramref name="source"/> is only used in messages.
    /// </summary>
    internal void AddSource(string text, string source)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _warnings = 0;

        string clean = StripComments(text);
        List<Block> blocks = SplitBlocks(clean, source);

        // Variables first, so a rule may use one defined further down the file.
        foreach (Block block in blocks)
        {
            if (!IsRoot(block.Selector))
                continue;

            List<int> ignoredLines;
            foreach (KeyValuePair<string, string> pair in SplitDeclarations(block, out ignoredLines))
            {
                if (pair.Key.StartsWith("--", StringComparison.Ordinal))
                    _variables[pair.Key] = pair.Value;
            }
        }

        foreach (Block block in blocks)
        {
            if (IsRoot(block.Selector))
                continue;

            if (block.Selector.StartsWith("@", StringComparison.Ordinal))
            {
                Warn(source, block.Line, "@rules are not supported; the block is skipped.");
                continue;
            }

            List<Declaration> declarations = ReadDeclarations(block, source);
            if (declarations.Count == 0)
                continue;

            foreach (string part in block.Selector.Split(','))
            {
                string selectorText = part.Trim();
                string problem;
                Selector selector = ParseSelector(selectorText, out problem);

                if (selector == null)
                {
                    Warn(source, block.Line, "Selector '" + selectorText + "' skipped: " + problem);
                    continue;
                }

                Rule rule = new Rule();
                rule.Selector = selector;
                rule.Order = _order++;
                rule.HasPseudo = selector.Parts[selector.Parts.Count - 1].Pseudos.Count > 0;
                rule.Declarations.AddRange(declarations);
                _rules.Add(rule);
            }
        }

        _sorted = false;
    }

    private static bool IsRoot(string selector)
    {
        return string.Equals(selector.Trim(), ":root", StringComparison.OrdinalIgnoreCase);
    }

    private void Warn(string source, int line, string message)
    {
        _warnings++;

        if (_warnings > MaxWarningsPerSource)
            return;

        string suffix = _warnings == MaxWarningsPerSource ? " (further warnings for this file are not shown)" : string.Empty;
        RelayDiagnostics.Warning("[Report] Style sheet " + source + ", line " + line.ToString(Inv) + ": " + message + suffix);
    }

    /// <summary>Replaces comments with spaces, keeping the line breaks so line numbers stay right.</summary>
    private static string StripComments(string text)
    {
        StringBuilder builder = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + 2;

                for (int k = i; k < stop; k++)
                    builder.Append(text[k] == '\n' ? '\n' : ' ');

                i = stop;
            }
            else
            {
                builder.Append(text[i]);
                i++;
            }
        }

        return builder.ToString();
    }

    private static int CountLines(string text, int upTo)
    {
        int lines = 0;
        for (int i = 0; i < upTo && i < text.Length; i++)
        {
            if (text[i] == '\n')
                lines++;
        }

        return lines;
    }

    private List<Block> SplitBlocks(string text, string source)
    {
        List<Block> blocks = new List<Block>();
        int position = 0;

        while (position < text.Length)
        {
            int open = text.IndexOf('{', position);
            if (open < 0)
                break;

            int close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                Warn(source, 1 + CountLines(text, open), "A '{' is never closed; the rest of the file is ignored.");
                break;
            }

            string selector = text.Substring(position, open - position);
            int selectorStart = position + (selector.Length - selector.TrimStart().Length);

            Block block = new Block();
            block.Selector = selector.Trim();
            block.Body = text.Substring(open + 1, close - open - 1);
            block.Line = 1 + CountLines(text, selectorStart);
            block.BodyLine = 1 + CountLines(text, open + 1);
            blocks.Add(block);

            position = close + 1;
        }

        return blocks;
    }

    /// <summary>The "name: value" pairs of a block, lower-case names, with the line each one is on.</summary>
    private static List<KeyValuePair<string, string>> SplitDeclarations(Block block, out List<int> lines)
    {
        List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
        lines = new List<int>();

        int offset = 0;
        foreach (string segment in block.Body.Split(';'))
        {
            int line = block.BodyLine + CountLines(block.Body, offset);
            offset += segment.Length + 1;

            int colon = segment.IndexOf(':');
            if (colon <= 0)
            {
                if (segment.Trim().Length > 0)
                {
                    pairs.Add(new KeyValuePair<string, string>(string.Empty, segment.Trim()));
                    lines.Add(line);
                }

                continue;
            }

            string name = segment.Substring(0, colon).Trim().ToLowerInvariant();
            string value = segment.Substring(colon + 1).Trim();
            pairs.Add(new KeyValuePair<string, string>(name, value));
            lines.Add(line);
        }

        return pairs;
    }

    private List<Declaration> ReadDeclarations(Block block, string source)
    {
        List<Declaration> result = new List<Declaration>();

        List<int> lines;
        List<KeyValuePair<string, string>> pairs = SplitDeclarations(block, out lines);

        for (int i = 0; i < pairs.Count; i++)
        {
            string name = pairs[i].Key;
            string value = pairs[i].Value;
            int line = lines[i];

            if (name.Length == 0)
            {
                Warn(source, line, "'" + value + "' is not a declaration (name: value) and is skipped.");
                continue;
            }

            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                Warn(source, line, "Variables can only be defined inside :root { }; '" + name + "' is skipped.");
                continue;
            }

            string resolved = ResolveVariables(value);
            if (resolved == null)
            {
                Warn(source, line, "'" + name + "': unknown variable or broken var() in '" + value + "'.");
                continue;
            }

            string problem;
            if (!ParseDeclaration(name, resolved, result, out problem))
                Warn(source, line, "'" + name + ": " + value + "' skipped: " + problem);
        }

        return result;
    }

    /// <summary>Substitutes var(--name) and var(--name, fallback). Null when a variable is unknown or the text is broken.</summary>
    private string ResolveVariables(string value)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            int start = value.IndexOf("var(", StringComparison.Ordinal);
            if (start < 0)
                return value;

            int depth = 0;
            int end = -1;

            for (int i = start + 3; i < value.Length; i++)
            {
                if (value[i] == '(')
                {
                    depth++;
                }
                else if (value[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
                return null;

            string inner = value.Substring(start + 4, end - start - 4);
            string name = inner;
            string fallback = null;

            int comma = inner.IndexOf(',');
            if (comma >= 0)
            {
                name = inner.Substring(0, comma);
                fallback = inner.Substring(comma + 1).Trim();
            }

            string replacement;
            if (!_variables.TryGetValue(name.Trim(), out replacement))
                replacement = fallback;

            if (replacement == null)
                return null;

            value = value.Substring(0, start) + replacement + value.Substring(end + 1);
        }

        // Variables that refer to each other without end.
        return null;
    }

    // ---------------------------------------------------------------- selectors

    private static bool IsIdentChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '-' || c == '_';
    }

    private static Selector ParseSelector(string text, out string problem)
    {
        problem = null;
        Selector selector = new Selector();

        int i = 0;
        int n = text.Length;
        char pending = '\0';

        while (i < n)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                if (pending == '\0' && selector.Parts.Count > 0)
                    pending = ' ';

                i++;
                continue;
            }

            if (c == '>')
            {
                pending = '>';
                i++;
                continue;
            }

            Compound compound = new Compound();
            int compoundStart = i;

            while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '>')
            {
                char k = text[i];

                if (k == '*')
                {
                    i++;
                }
                else if (k == '.' || k == '#' || k == ':')
                {
                    i++;
                    int identStart = i;

                    while (i < n && IsIdentChar(text[i]))
                        i++;

                    string ident = text.Substring(identStart, i - identStart);
                    if (ident.Length == 0)
                    {
                        problem = "a name is missing after '" + k + "'.";
                        return null;
                    }

                    if (k == '.')
                    {
                        compound.Classes.Add(ident);
                    }
                    else if (k == '#')
                    {
                        compound.Id = ident;
                    }
                    else
                    {
                        string pseudo = ident.ToLowerInvariant();
                        if (pseudo != "hover" && pseudo != "active" && pseudo != "disabled" && pseudo != "enabled")
                        {
                            problem = "the pseudo-class ':" + ident + "' is not supported (use :hover, :active, :disabled or :enabled).";
                            return null;
                        }

                        compound.Pseudos.Add(pseudo);
                    }
                }
                else if (IsIdentChar(k) && i == compoundStart)
                {
                    int identStart = i;

                    while (i < n && IsIdentChar(text[i]))
                        i++;

                    compound.Type = text.Substring(identStart, i - identStart);
                }
                else
                {
                    problem = "unexpected '" + k + "'.";
                    return null;
                }
            }

            if (selector.Parts.Count > 0)
                selector.Combinators.Add(pending == '\0' ? ' ' : pending);

            selector.Parts.Add(compound);
            pending = '\0';
        }

        if (selector.Parts.Count == 0)
        {
            problem = "it is empty.";
            return null;
        }

        if (pending == '>')
        {
            problem = "it ends with '>'.";
            return null;
        }

        for (int p = 0; p < selector.Parts.Count - 1; p++)
        {
            if (selector.Parts[p].Pseudos.Count > 0)
            {
                problem = "pseudo-classes are only supported on the last part of a selector.";
                return null;
            }
        }

        int specificity = 0;
        foreach (Compound part in selector.Parts)
        {
            if (part.Id != null)
                specificity += 10000;

            specificity += (part.Classes.Count + part.Pseudos.Count) * 100;

            if (part.Type != null)
                specificity += 1;
        }

        selector.Specificity = specificity;
        return selector;
    }
}
