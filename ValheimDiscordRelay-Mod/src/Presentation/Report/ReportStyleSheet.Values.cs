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
