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
    internal sealed partial class ReportStyleSheet
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

    }
}
