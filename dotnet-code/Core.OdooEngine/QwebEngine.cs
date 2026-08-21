using System.Collections;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Core.OdooEngine;

// ponytail: covers dotted-path field access + ==,!=,>,>=,<,<=,&&,||,! for t-if/t-esc/t-foreach.
// No arithmetic, no method calls, no 'in'. Add a real parser (or a scripting dependency) if reports need more.
public static class QwebExpr
{
    public static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        double d => d != 0,
        int i => i != 0,
        ICollection c => c.Count > 0,
        _ => true
    };

    public static object? Eval(string expr, Dictionary<string, object?> ctx) => new Parser(expr, ctx).ParseOr();

    public static object? Resolve(string path, Dictionary<string, object?> ctx)
    {
        object? current = ctx;
        foreach (var part in path.Split('.'))
        {
            switch (current)
            {
                case Dictionary<string, object?> d1:
                    current = d1.TryGetValue(part, out var v1) ? v1 : null; break;
                case object[] tuple when tuple.Length == 2:
                    current = part == "id" ? tuple[0] : tuple[1]; break;
                default:
                    return null;
            }
        }
        return current;
    }

    private class Parser(string s, Dictionary<string, object?> ctx)
    {
        private int _i;

        private void SkipWs() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }

        private bool Match(string tok)
        {
            SkipWs();
            if (_i + tok.Length <= s.Length && s.Substring(_i, tok.Length) == tok) { _i += tok.Length; return true; }
            return false;
        }

        public object? ParseOr()
        {
            var left = ParseAnd();
            while (Match("||")) left = IsTruthy(left) || IsTruthy(ParseAnd());
            return left;
        }

        private object? ParseAnd()
        {
            var left = ParseNot();
            while (Match("&&")) left = IsTruthy(left) && IsTruthy(ParseNot());
            return left;
        }

        private object? ParseNot() => Match("!") ? !IsTruthy(ParseNot()) : ParseComparison();

        private object? ParseComparison()
        {
            var left = ParsePrimary();
            foreach (var op in new[] { "==", "!=", ">=", "<=", ">", "<" })
            {
                if (Match(op)) return Compare(op, left, ParsePrimary());
            }
            return left;
        }

        private object? ParsePrimary()
        {
            SkipWs();
            if (_i >= s.Length) return null;
            var c = s[_i];

            if (c == '(')
            {
                _i++;
                var v = ParseOr();
                SkipWs();
                if (_i < s.Length && s[_i] == ')') _i++;
                return v;
            }
            if (c is '\'' or '"')
            {
                var quote = c; _i++;
                var start = _i;
                while (_i < s.Length && s[_i] != quote) _i++;
                var str = s.Substring(start, _i - start);
                if (_i < s.Length) _i++;
                return str;
            }
            if (char.IsDigit(c) || (c == '-' && _i + 1 < s.Length && char.IsDigit(s[_i + 1])))
            {
                var start = _i;
                _i++;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
                return double.Parse(s[start.._i], CultureInfo.InvariantCulture);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = _i;
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] is '_' or '.')) _i++;
                var ident = s[start.._i];
                return ident switch
                {
                    "true" => true,
                    "false" => false,
                    "null" or "None" => null,
                    _ => Resolve(ident, ctx)
                };
            }
            return null;
        }

        private static object? Compare(string op, object? left, object? right)
        {
            if (op == "==") return LooseEquals(left, right);
            if (op == "!=") return !LooseEquals(left, right);
            var (ld, rd) = (ToNumber(left), ToNumber(right));
            if (ld == null || rd == null) return false;
            return op switch { ">" => ld > rd, "<" => ld < rd, ">=" => ld >= rd, "<=" => ld <= rd, _ => false };
        }

        private static bool LooseEquals(object? a, object? b)
        {
            if (a == null || b == null) return a == null && b == null;
            var (na, nb) = (ToNumber(a), ToNumber(b));
            return na != null && nb != null ? na == nb : a.ToString() == b.ToString();
        }

        private static double? ToNumber(object? v) => v switch
        {
            double d => d,
            int i => i,
            string s2 => double.TryParse(s2, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null
        };
    }
}

public static class QwebEngine
{
    public static string Render(string archXml, Dictionary<string, object?> ctx)
    {
        var sb = new StringBuilder();
        RenderNode(XDocument.Parse(archXml).Root!, ctx, sb);
        return sb.ToString();
    }

    private static void RenderNode(XElement el, Dictionary<string, object?> ctx, StringBuilder sb)
    {
        var tForeach = el.Attribute("t-foreach")?.Value;
        if (tForeach != null)
        {
            var tAs = el.Attribute("t-as")?.Value ?? "item";
            if (QwebExpr.Eval(tForeach, ctx) is IEnumerable seq and not string)
            {
                var idx = 0;
                foreach (var item in seq)
                {
                    var loopCtx = new Dictionary<string, object?>(ctx) { [tAs] = item, [$"{tAs}_index"] = idx };
                    RenderElement(el, loopCtx, sb);
                    idx++;
                }
            }
            return;
        }
        RenderElement(el, ctx, sb);
    }

    private static void RenderElement(XElement el, Dictionary<string, object?> ctx, StringBuilder sb)
    {
        var isTag = el.Name.LocalName != "t";
        if (isTag)
        {
            sb.Append('<').Append(el.Name.LocalName);
            foreach (var attr in el.Attributes())
            {
                var name = attr.Name.LocalName;
                if (name is "t-if" or "t-elif" or "t-else" or "t-esc" or "t-out" or "t-raw" or "t-field" or "t-foreach" or "t-as" or "t-name") continue;
                if (name.StartsWith("t-att-"))
                    sb.Append(' ').Append(name["t-att-".Length..]).Append("=\"").Append(Escape(Stringify(QwebExpr.Eval(attr.Value, ctx)))).Append('"');
                else
                    sb.Append(' ').Append(name).Append("=\"").Append(attr.Value).Append('"');
            }
            sb.Append('>');
        }

        var expr = el.Attribute("t-esc")?.Value ?? el.Attribute("t-out")?.Value ?? el.Attribute("t-field")?.Value;
        var rawExpr = el.Attribute("t-raw")?.Value;
        if (expr != null) sb.Append(Escape(Stringify(QwebExpr.Eval(expr, ctx))));
        else if (rawExpr != null) sb.Append(Stringify(QwebExpr.Eval(rawExpr, ctx)));
        else RenderChildren(el, ctx, sb);

        if (isTag) sb.Append("</").Append(el.Name.LocalName).Append('>');
    }

    private static void RenderChildren(XElement parent, Dictionary<string, object?> ctx, StringBuilder sb)
    {
        var prevIfMatched = false;
        foreach (var node in parent.Nodes())
        {
            if (node is XText text) { sb.Append(text.Value); continue; }
            if (node is not XElement child) continue;

            var tIf = child.Attribute("t-if")?.Value;
            var tElif = child.Attribute("t-elif")?.Value;
            var isElse = child.Attribute("t-else") != null;

            if (tIf != null)
            {
                prevIfMatched = QwebExpr.IsTruthy(QwebExpr.Eval(tIf, ctx));
                if (prevIfMatched) RenderNode(child, ctx, sb);
            }
            else if (tElif != null)
            {
                if (!prevIfMatched && QwebExpr.IsTruthy(QwebExpr.Eval(tElif, ctx))) { prevIfMatched = true; RenderNode(child, ctx, sb); }
            }
            else if (isElse)
            {
                if (!prevIfMatched) RenderNode(child, ctx, sb);
                prevIfMatched = false;
            }
            else
            {
                prevIfMatched = false;
                RenderNode(child, ctx, sb);
            }
        }
    }

    private static string Stringify(object? val) => val switch
    {
        null => "",
        bool b => b ? "Yes" : "No",
        object[] tuple when tuple.Length == 2 => tuple[1]?.ToString() ?? "",
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        _ => val.ToString() ?? ""
    };

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
