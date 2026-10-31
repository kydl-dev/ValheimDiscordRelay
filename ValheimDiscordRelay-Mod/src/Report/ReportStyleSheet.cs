using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace ValheimDiscordRelay.Report
{
    /// <summary>
    /// A small USS / CSS reader for the report window.
    ///
    /// Unity's own style sheets (.uss) can only be loaded as assets that the Unity Editor has imported, and the runtime has
    /// no public way to read USS text. A mod has no Editor, so this class does the job itself: it reads style sheet text,
    /// works out which rules match each element, and applies the winning declarations as inline styles.
    ///
    /// Supported (a subset of USS):
    ///   - Selectors: Type, .class, #name, * and combinations (Label.vdr-cell), descendant (a b) and child (a &gt; b)
    ///     combinators, comma lists, and the pseudo-classes :hover, :active, :disabled and :enabled on the last part.
    ///   - Variables: --name: value; inside :root { }, used as var(--name) or var(--name, fallback).
    ///   - Properties: every property of Unity's IStyle whose value is a colour, a length (px, %, auto, none), a number or a
    ///     keyword (names as in USS, for example background-color, font-size, -unity-font-style, -unity-text-align,
    ///     margin, padding, border-width, border-color, border-radius, opacity). "initial" resets a property.
    ///   - Fonts: -unity-font-definition: heading | heading-bold | body | body-bold (Valheim's own fonts, see ReportFonts).
    ///   - Cascade: higher specificity wins (#name, then .class and :pseudo, then Type); on a tie the later rule wins.
    ///
    /// Not supported: @rules (@import, @media ...), animations and transitions, url() and resource() values, the "flex" and
    /// "border" shorthands, attribute selectors, sibling combinators, :first-child and the like. Anything that is not
    /// understood is logged as a warning with its line number and skipped; the rest of the sheet still applies.
    ///
    /// Values are applied as inline styles. The first time a rule changes a property its previous inline value is kept, and
    /// it is put back when the rule stops applying (a :hover that ends, a class that is removed, a sheet that is reloaded).
    /// </summary>
    internal sealed class ReportStyleSheet
    {
        private const int MaxWarningsPerSource = 25;

        private sealed class Declaration
        {
            public string Property;
            public object Value;
        }

        private sealed class Compound
        {
            public string Type;
            public string Id;
            public readonly List<string> Classes = new List<string>();
            public readonly List<string> Pseudos = new List<string>();
        }

        private sealed class Selector
        {
            public readonly List<Compound> Parts = new List<Compound>();

            // Combinators[i] sits between Parts[i] and Parts[i + 1]: a space (descendant) or '>' (child).
            public readonly List<char> Combinators = new List<char>();
            public int Specificity;
        }

        private sealed class Rule
        {
            public Selector Selector;
            public readonly List<Declaration> Declarations = new List<Declaration>();
            public int Order;
            public bool HasPseudo;
        }

        private struct Block
        {
            public string Selector;
            public string Body;
            public int Line;
            public int BodyLine;
        }

        /// <summary>
        /// What is remembered per element. Shared by all sheets, so a reloaded sheet can undo what the old one applied.
        /// </summary>
        private sealed class ElementInfo
        {
            public ReportStyleSheet Owner;
            public bool Hover;
            public bool Active;
            public bool HandlersAdded;

            // The inline value each touched property had before a rule changed it.
            public readonly Dictionary<string, object> Restore = new Dictionary<string, object>();
            public readonly HashSet<string> Applied = new HashSet<string>();
        }

        private static readonly ConditionalWeakTable<VisualElement, ElementInfo> Infos =
            new ConditionalWeakTable<VisualElement, ElementInfo>();

        private static readonly Dictionary<string, PropertyInfo> StyleProperties = BuildPropertyTable();

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly List<Rule> _rules = new List<Rule>();
        private readonly Dictionary<string, string> _variables = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _sorted = true;
        private int _order;
        private int _warnings;
        private static float _nextApplyFailureLog;

        /// <summary>How many rules were read. Zero means the sheet does nothing.</summary>
        internal int RuleCount
        {
            get { return _rules.Count; }
        }

        // ---------------------------------------------------------------- applying

        /// <summary>Applies the sheet to an element and everything below it.</summary>
        internal void Apply(VisualElement root)
        {
            if (root == null)
                return;

            ApplyTo(root);

            foreach (VisualElement child in root.Children())
                Apply(child);
        }

        /// <summary>
        /// Applies the sheet to one element again: after a class was added or removed, or its enabled state changed.
        /// </summary>
        internal void Refresh(VisualElement element)
        {
            if (element != null)
                ApplyTo(element);
        }

        private void ApplyTo(VisualElement element)
        {
            ElementInfo info = GetInfo(element);
            info.Owner = this;

            if (!_sorted)
            {
                _rules.Sort(delegate (Rule a, Rule b)
                {
                    int bySpecificity = a.Selector.Specificity.CompareTo(b.Selector.Specificity);
                    return bySpecificity != 0 ? bySpecificity : a.Order.CompareTo(b.Order);
                });
                _sorted = true;
            }

            // The winning value of every property, from the rules that match right now (lowest priority first).
            Dictionary<string, object> final = new Dictionary<string, object>();
            bool hasPseudoRule = false;

            for (int i = 0; i < _rules.Count; i++)
            {
                Rule rule = _rules[i];
                Selector selector = rule.Selector;

                if (!MatchesFrom(selector, selector.Parts.Count - 1, element))
                    continue;

                if (rule.HasPseudo)
                {
                    hasPseudoRule = true;

                    if (!PseudosHold(selector.Parts[selector.Parts.Count - 1], element, info))
                        continue;
                }

                for (int d = 0; d < rule.Declarations.Count; d++)
                    final[rule.Declarations[d].Property] = rule.Declarations[d].Value;
            }

            // Properties an earlier pass set that no rule sets now go back to what they were.
            List<string> stale = null;
            foreach (string property in info.Applied)
            {
                if (!final.ContainsKey(property))
                {
                    if (stale == null)
                        stale = new List<string>();

                    stale.Add(property);
                }
            }

            if (stale != null)
            {
                foreach (string property in stale)
                    RestoreProperty(element, info, property);
            }

            foreach (KeyValuePair<string, object> pair in final)
            {
                try
                {
                    PropertyInfo property = StyleProperties[pair.Key];

                    if (!info.Restore.ContainsKey(pair.Key))
                        info.Restore[pair.Key] = property.GetValue(element.style, null);

                    property.SetValue(element.style, pair.Value, null);
                    info.Applied.Add(pair.Key);
                }
                catch (Exception ex)
                {
                    LogApplyFailure(pair.Key, ex);
                }
            }

            if (hasPseudoRule && !info.HandlersAdded)
                AddStateHandlers(element, info);
        }

        private static void RestoreProperty(VisualElement element, ElementInfo info, string property)
        {
            try
            {
                object original;
                if (info.Restore.TryGetValue(property, out original))
                    StyleProperties[property].SetValue(element.style, original, null);
            }
            catch (Exception ex)
            {
                LogApplyFailure(property, ex);
            }

            info.Restore.Remove(property);
            info.Applied.Remove(property);
        }

        private static void LogApplyFailure(string property, Exception ex)
        {
            // Throttled: the same failure would repeat for every element.
            float now = Time.unscaledTime;
            if (now < _nextApplyFailureLog)
                return;

            _nextApplyFailureLog = now + 30f;
            RelayDiagnostics.Warning("[Report] The style property '" + property + "' could not be applied: " + ex.Message);
        }

        private static ElementInfo GetInfo(VisualElement element)
        {
            ElementInfo info;
            if (!Infos.TryGetValue(element, out info))
            {
                info = new ElementInfo();
                Infos.Add(element, info);
            }

            return info;
        }

        /// <summary>
        /// :hover and :active need to know what the pointer does. The handlers are added only to elements that some rule
        /// with a pseudo-class matches, so the rest of the window costs nothing.
        /// </summary>
        private static void AddStateHandlers(VisualElement element, ElementInfo info)
        {
            info.HandlersAdded = true;

            element.RegisterCallback<PointerEnterEvent>(delegate
            {
                info.Hover = true;
                info.Owner.ApplyTo(element);
            });

            element.RegisterCallback<PointerLeaveEvent>(delegate
            {
                // A pressed element keeps :active until the button is released, also when the pointer wandered off.
                info.Hover = false;
                info.Owner.ApplyTo(element);
            });

            // Trickle-down: a button handles its own click and may stop these events before a normal handler sees them.
            element.RegisterCallback<PointerDownEvent>(delegate
            {
                info.Active = true;
                info.Owner.ApplyTo(element);
            }, TrickleDown.TrickleDown);

            element.RegisterCallback<PointerUpEvent>(delegate (PointerUpEvent evt)
            {
                info.Active = false;
                info.Hover = element.worldBound.Contains((Vector2)evt.position);
                info.Owner.ApplyTo(element);
            }, TrickleDown.TrickleDown);

            element.RegisterCallback<PointerCaptureOutEvent>(delegate
            {
                info.Active = false;
                info.Owner.ApplyTo(element);
            });
        }

        // ---------------------------------------------------------------- matching

        private static bool MatchesCompound(Compound compound, VisualElement element)
        {
            if (compound.Type != null && !string.Equals(element.GetType().Name, compound.Type, StringComparison.Ordinal))
                return false;

            if (compound.Id != null && !string.Equals(element.name, compound.Id, StringComparison.Ordinal))
                return false;

            for (int i = 0; i < compound.Classes.Count; i++)
            {
                if (!element.ClassListContains(compound.Classes[i]))
                    return false;
            }

            return true;
        }

        /// <summary>Does the element match Parts[index], with the parts before it matching its ancestors? (Pseudo-classes are checked separately.)</summary>
        private static bool MatchesFrom(Selector selector, int index, VisualElement element)
        {
            if (!MatchesCompound(selector.Parts[index], element))
                return false;

            if (index == 0)
                return true;

            VisualElement parent = element.parent;

            if (selector.Combinators[index - 1] == '>')
                return parent != null && MatchesFrom(selector, index - 1, parent);

            while (parent != null)
            {
                if (MatchesFrom(selector, index - 1, parent))
                    return true;

                parent = parent.parent;
            }

            return false;
        }

        private static bool PseudosHold(Compound compound, VisualElement element, ElementInfo info)
        {
            for (int i = 0; i < compound.Pseudos.Count; i++)
            {
                switch (compound.Pseudos[i])
                {
                    case "hover":
                        if (!info.Hover)
                            return false;
                        break;

                    case "active":
                        if (!info.Active)
                            return false;
                        break;

                    case "disabled":
                        if (element.enabledInHierarchy)
                            return false;
                        break;

                    case "enabled":
                        if (!element.enabledInHierarchy)
                            return false;
                        break;

                    default:
                        return false;
                }
            }

            return true;
        }

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

        // ---------------------------------------------------------------- values

        private static Dictionary<string, PropertyInfo> BuildPropertyTable()
        {
            Dictionary<string, PropertyInfo> table = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

            foreach (PropertyInfo property in typeof(IStyle).GetProperties())
            {
                if (property.CanRead && property.CanWrite)
                    table[property.Name] = property;
            }

            return table;
        }

        /// <summary>USS name to IStyle property name: background-color to backgroundColor, -unity-text-align to unityTextAlign.</summary>
        private static string MapName(string cssName)
        {
            if (cssName == "-unity-font-style")
                return "unityFontStyleAndWeight";

            string name = cssName.TrimStart('-');
            StringBuilder builder = new StringBuilder(name.Length);
            bool upper = false;

            foreach (char c in name)
            {
                if (c == '-')
                {
                    upper = true;
                }
                else
                {
                    builder.Append(upper ? char.ToUpperInvariant(c) : c);
                    upper = false;
                }
            }

            return builder.ToString();
        }

        private bool ParseDeclaration(string name, string value, List<Declaration> output, out string problem)
        {
            switch (name)
            {
                case "margin":
                    return ExpandBox(new[] { "marginTop", "marginRight", "marginBottom", "marginLeft" }, value, output, out problem);

                case "padding":
                    return ExpandBox(new[] { "paddingTop", "paddingRight", "paddingBottom", "paddingLeft" }, value, output, out problem);

                case "border-width":
                    return ExpandBox(new[] { "borderTopWidth", "borderRightWidth", "borderBottomWidth", "borderLeftWidth" }, value, output, out problem);

                case "border-color":
                    return ExpandBox(new[] { "borderTopColor", "borderRightColor", "borderBottomColor", "borderLeftColor" }, value, output, out problem);

                case "border-radius":
                    return ExpandBox(new[] { "borderTopLeftRadius", "borderTopRightRadius", "borderBottomRightRadius", "borderBottomLeftRadius" }, value, output, out problem);

                default:
                    return ParseSingle(MapName(name), value, output, out problem);
            }
        }

        /// <summary>The 1 to 4 value shorthand (top right bottom left; for border-radius top-left top-right bottom-right bottom-left).</summary>
        private bool ExpandBox(string[] properties, string value, List<Declaration> output, out string problem)
        {
            List<string> tokens = Tokenize(value);
            problem = null;

            if (tokens.Count < 1 || tokens.Count > 4)
            {
                problem = "expected one to four values.";
                return false;
            }

            string top = tokens[0];
            string right = tokens.Count > 1 ? tokens[1] : top;
            string bottom = tokens.Count > 2 ? tokens[2] : top;
            string left = tokens.Count > 3 ? tokens[3] : right;
            string[] values = { top, right, bottom, left };

            List<Declaration> parsed = new List<Declaration>();
            for (int i = 0; i < 4; i++)
            {
                if (!ParseSingle(properties[i], values[i], parsed, out problem))
                    return false;
            }

            output.AddRange(parsed);
            return true;
        }

        private bool ParseSingle(string property, string value, List<Declaration> output, out string problem)
        {
            problem = null;

            PropertyInfo info;
            if (!StyleProperties.TryGetValue(property, out info))
            {
                problem = "unknown property.";
                return false;
            }

            object parsed = ParseValue(info.PropertyType, value);
            if (parsed == null)
            {
                problem = "the value is not valid for this property.";
                return false;
            }

            Declaration declaration = new Declaration();
            declaration.Property = property;
            declaration.Value = parsed;
            output.Add(declaration);
            return true;
        }

        /// <summary>Splits on spaces that are not inside parentheses (so rgba(1, 2, 3, 4) stays one token).</summary>
        private static List<string> Tokenize(string value)
        {
            List<string> tokens = new List<string>();
            StringBuilder current = new StringBuilder();
            int depth = 0;

            foreach (char c in value)
            {
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;

                if (char.IsWhiteSpace(c) && depth <= 0)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Length = 0;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());

            return tokens;
        }

        /// <summary>The boxed StyleColor, StyleLength, StyleFloat or StyleEnum for a value, or null when it does not fit.</summary>
        private static object ParseValue(Type styleType, string value)
        {
            value = value.Trim();
            string lower = value.ToLowerInvariant();

            try
            {
                if (lower == "initial")
                    return Activator.CreateInstance(styleType, StyleKeyword.Initial);

                if (styleType == typeof(StyleColor))
                {
                    Color color;
                    return TryParseColor(value, out color) ? (object)new StyleColor(color) : null;
                }

                if (styleType == typeof(StyleLength))
                {
                    if (lower == "auto")
                        return new StyleLength(StyleKeyword.Auto);

                    if (lower == "none")
                        return new StyleLength(StyleKeyword.None);

                    Length length;
                    return TryParseLength(lower, out length) ? (object)new StyleLength(length) : null;
                }

                if (styleType == typeof(StyleFloat))
                {
                    // USS writes widths with a unit (border-width: 1px); the bare number (border-width: 1) is accepted too.
                    string digits = lower.EndsWith("px", StringComparison.Ordinal) ? lower.Substring(0, lower.Length - 2) : lower;

                    float number;
                    return TryParseNumber(digits, out number) ? (object)new StyleFloat(number) : null;
                }

                if (styleType == typeof(StyleFontDefinition))
                {
                    // Only Valheim's own fonts, by keyword (see ReportFonts). A font the game does not have unsets the
                    // property instead, so the element keeps the font it inherits from the window.
                    if (!ReportFonts.IsKeyword(lower))
                        return null;

                    Font font = ReportFonts.Get(lower);
                    return font != null
                        ? (object)new StyleFontDefinition(FontDefinition.FromFont(font))
                        : new StyleFontDefinition(StyleKeyword.Null);
                }

                if (styleType.IsGenericType && styleType.GetGenericTypeDefinition() == typeof(StyleEnum<>))
                {
                    Type enumType = styleType.GetGenericArguments()[0];
                    string key = lower.Replace("-", string.Empty);

                    foreach (string enumName in Enum.GetNames(enumType))
                    {
                        if (string.Equals(enumName, key, StringComparison.OrdinalIgnoreCase))
                            return Activator.CreateInstance(styleType, Enum.Parse(enumType, enumName));
                    }
                }
            }
            catch (Exception)
            {
                // A type this reader cannot build: treated as an unsupported value.
            }

            return null;
        }

        private static bool TryParseNumber(string text, out float number)
        {
            return float.TryParse(text, NumberStyles.Float, Inv, out number);
        }

        private static bool TryParseLength(string lower, out Length length)
        {
            length = default(Length);
            float number;

            if (lower.EndsWith("%", StringComparison.Ordinal))
            {
                if (!TryParseNumber(lower.Substring(0, lower.Length - 1), out number))
                    return false;

                length = new Length(number, LengthUnit.Percent);
                return true;
            }

            string digits = lower.EndsWith("px", StringComparison.Ordinal) ? lower.Substring(0, lower.Length - 2) : lower;
            if (!TryParseNumber(digits, out number))
                return false;

            length = new Length(number, LengthUnit.Pixel);
            return true;
        }

        /// <summary>#rgb, #rgba, #rrggbb, #rrggbbaa, rgb(r, g, b), rgba(r, g, b, a) (r, g, b 0 to 255, a 0 to 1) and colour names.</summary>
        private static bool TryParseColor(string text, out Color color)
        {
            color = Color.clear;
            string lower = text.Trim().ToLowerInvariant();

            if (lower.StartsWith("rgb", StringComparison.Ordinal))
            {
                int open = lower.IndexOf('(');
                int close = lower.LastIndexOf(')');
                if (open < 0 || close < open)
                    return false;

                string[] parts = lower.Substring(open + 1, close - open - 1)
                    .Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 3 && parts.Length != 4)
                    return false;

                float r, g, b;
                float a = 1f;

                if (!TryParseNumber(parts[0], out r) || !TryParseNumber(parts[1], out g) || !TryParseNumber(parts[2], out b))
                    return false;

                if (parts.Length == 4)
                {
                    string alpha = parts[3];
                    bool percent = alpha.EndsWith("%", StringComparison.Ordinal);

                    if (!TryParseNumber(percent ? alpha.Substring(0, alpha.Length - 1) : alpha, out a))
                        return false;

                    if (percent)
                        a /= 100f;
                }

                color = new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f), Mathf.Clamp01(a));
                return true;
            }

            return ColorUtility.TryParseHtmlString(lower, out color);
        }
    }
}
