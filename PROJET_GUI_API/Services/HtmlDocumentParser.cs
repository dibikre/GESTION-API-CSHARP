using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ApiTester.Models;

namespace ApiTester.Services;

internal sealed class HtmlParsedAttribute
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public int NameStartOffset { get; init; }
    public int ValueStartOffset { get; init; }
    public int ValueEndOffset { get; init; }
    public char? QuoteChar { get; init; }
    public bool HasExplicitValue { get; init; }
}

internal sealed class HtmlParsedElement
{
    public required string TagName { get; init; }
    public int StartOffset { get; init; }
    public int OpenTagEndOffset { get; init; }
    public int EndOffset { get; set; }
    public bool IsVoidOrSelfClosing { get; init; }
    public bool IsRawText { get; init; }
    public HtmlParsedElement? Parent { get; init; }
    public List<HtmlParsedAttribute> Attributes { get; } = [];
    public List<HtmlParsedElement> Children { get; } = [];

    public string? GetAttribute(string name)
    {
        foreach (var attr in Attributes)
        {
            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return attr.Value;
            }
        }

        return null;
    }
}

internal sealed class HtmlSelectionContext
{
    public required HtmlParsedElement Element { get; init; }
    public HtmlParsedAttribute? Attribute { get; init; }
    public required string HtmlPath { get; init; }
    public required string CssSelector { get; init; }
    public bool IsInsideAttribute => Attribute is not null;
    public bool IsInsideRawText => !IsInsideAttribute && Element.IsRawText;
}

internal sealed class HtmlParseResult
{
    public required List<HtmlParsedElement> Roots { get; init; }
    public required Dictionary<string, int> IdCounts { get; init; }
    public required Dictionary<string, int> MetaPropertyCounts { get; init; }
    public required Dictionary<string, int> MetaNameCounts { get; init; }
    public required Dictionary<string, int> LinkRelCounts { get; init; }
}

internal static class HtmlDocumentParser
{
    private const int MaxElements = 25_000;
    private const int PreviewMaxChars = 72;

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "basefont", "bgsound", "br", "col", "command", "embed",
        "frame", "hr", "img", "input", "isindex", "keygen", "link", "menuitem",
        "meta", "param", "source", "track", "wbr"
    };

    private static readonly HashSet<string> RawTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title", "noscript", "xmp"
    };

    private static readonly HashSet<string> BlockTagsClosingP = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "details", "dialog", "dd", "div",
        "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2",
        "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "main", "nav", "ol",
        "p", "pre", "section", "table", "ul"
    };

    public static HtmlParseResult Parse(string html)
    {
        var roots = new List<HtmlParsedElement>();
        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var metaPropertyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var metaNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var linkRelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(html))
        {
            return new HtmlParseResult
            {
                Roots = roots,
                IdCounts = idCounts,
                MetaPropertyCounts = metaPropertyCounts,
                MetaNameCounts = metaNameCounts,
                LinkRelCounts = linkRelCounts
            };
        }

        var stack = new List<HtmlParsedElement>();
        var len = html.Length;
        var pos = 0;
        var elementCount = 0;

        while (pos < len && elementCount < MaxElements)
        {
            var lt = html.IndexOf('<', pos);
            if (lt < 0)
            {
                break;
            }

            if (lt + 1 >= len)
            {
                break;
            }

            var next = html[lt + 1];

            // Commentaire <!-- ... -->
            if (next == '!' && lt + 3 < len && html[lt + 2] == '-' && html[lt + 3] == '-')
            {
                var commentEnd = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                pos = commentEnd >= 0 ? commentEnd + 3 : len;
                continue;
            }

            // Déclaration <!DOCTYPE ...> ou <![CDATA[...]]>
            if (next == '!')
            {
                if (lt + 8 < len && html.AsSpan(lt, 9).Equals("<![CDATA[", StringComparison.OrdinalIgnoreCase))
                {
                    var cdataEnd = html.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                    pos = cdataEnd >= 0 ? cdataEnd + 3 : len;
                    continue;
                }

                var declEnd = html.IndexOf('>', lt + 2);
                pos = declEnd >= 0 ? declEnd + 1 : len;
                continue;
            }

            // Instruction de traitement <?...?>
            if (next == '?')
            {
                var piEnd = html.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                if (piEnd < 0)
                {
                    piEnd = html.IndexOf('>', lt + 2);
                    pos = piEnd >= 0 ? piEnd + 1 : len;
                }
                else
                {
                    pos = piEnd + 2;
                }
                continue;
            }

            // Balise fermante </tag>
            if (next == '/')
            {
                var nameStart = lt + 2;
                while (nameStart < len && char.IsWhiteSpace(html[nameStart]))
                {
                    nameStart++;
                }

                var nameEnd = nameStart;
                while (nameEnd < len && IsTagNameChar(html[nameEnd]))
                {
                    nameEnd++;
                }

                var closeGt = html.IndexOf('>', nameEnd);
                var closeEndOffset = closeGt >= 0 ? closeGt + 1 : len;

                if (nameEnd > nameStart)
                {
                    var closeTagName = html[nameStart..nameEnd];
                    CloseMatchingTagOnStack(stack, closeTagName, lt, closeEndOffset);
                }

                pos = closeEndOffset;
                continue;
            }

            // Balise ouvrante <tag ...>
            if (!IsTagNameStartChar(next))
            {
                pos = lt + 1;
                continue;
            }

            var tagStart = lt + 1;
            var tagEnd = tagStart;
            while (tagEnd < len && IsTagNameChar(html[tagEnd]))
            {
                tagEnd++;
            }

            var tagName = html[tagStart..tagEnd].ToLowerInvariant();
            var attrs = ParseAttributes(html, tagEnd, out var openTagGt, out var selfClosing);
            var openTagEndOffset = openTagGt >= 0 ? openTagGt + 1 : len;

            AutoCloseImplicitTags(stack, tagName, lt);

            var parent = stack.Count > 0 ? stack[^1] : null;
            var isVoid = selfClosing || VoidTags.Contains(tagName);
            var isRawText = !isVoid && RawTextTags.Contains(tagName);

            var element = new HtmlParsedElement
            {
                TagName = tagName,
                StartOffset = lt,
                OpenTagEndOffset = openTagEndOffset,
                EndOffset = openTagEndOffset,
                IsVoidOrSelfClosing = isVoid,
                IsRawText = isRawText,
                Parent = parent
            };

            foreach (var attr in attrs)
            {
                element.Attributes.Add(attr);
            }

            RegisterElementStats(element, idCounts, metaPropertyCounts, metaNameCounts, linkRelCounts);

            if (parent is not null)
            {
                parent.Children.Add(element);
            }
            else
            {
                roots.Add(element);
            }

            elementCount++;

            if (isVoid)
            {
                pos = openTagEndOffset;
                continue;
            }

            if (isRawText)
            {
                var rawClose = FindRawTextClosingTag(html, openTagEndOffset, tagName, out var rawEndOffset);
                if (rawClose >= 0)
                {
                    element.EndOffset = rawEndOffset;
                    pos = rawEndOffset;
                }
                else
                {
                    element.EndOffset = len;
                    pos = len;
                }
                continue;
            }

            stack.Add(element);
            pos = openTagEndOffset;
        }

        // Fermer les balises restées ouvertes à la fin du document
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            stack[i].EndOffset = len;
        }

        return new HtmlParseResult
        {
            Roots = roots,
            IdCounts = idCounts,
            MetaPropertyCounts = metaPropertyCounts,
            MetaNameCounts = metaNameCounts,
            LinkRelCounts = linkRelCounts
        };
    }

    public static ObservableCollection<ExtractorNode> BuildExtractorTree(string html, HtmlParseResult parsed)
    {
        var result = new ObservableCollection<ExtractorNode>();
        if (parsed.Roots.Count == 0)
        {
            return result;
        }

        // Si un élément <html> racine existe parmi des commentaires/nœuds parasites, le mettre en avant
        foreach (var root in parsed.Roots)
        {
            result.Add(BuildExtractorNode(html, root, parsed, depth: 0));
        }

        return result;
    }

    public static HtmlSelectionContext? LocateSelection(string html, int selectionStart, int selectionLength)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var safeStart = Math.Clamp(selectionStart, 0, html.Length);
        var safeEnd = Math.Clamp(safeStart + Math.Max(0, selectionLength), safeStart, html.Length);

        var parsed = Parse(html);
        if (parsed.Roots.Count == 0)
        {
            return null;
        }

        HtmlParsedElement? best = null;
        var candidates = parsed.Roots;
        while (candidates.Count > 0)
        {
            HtmlParsedElement? matched = null;
            foreach (var child in candidates)
            {
                if (safeStart >= child.StartOffset && safeEnd <= child.EndOffset)
                {
                    matched = child;
                    break;
                }
            }

            if (matched is null)
            {
                break;
            }

            best = matched;
            candidates = matched.Children;
        }

        if (best is null)
        {
            return null;
        }

        var elementXPath = BuildHtmlPath(best, parsed);
        var elementCss = BuildCssSelector(best, parsed);

        foreach (var attr in best.Attributes)
        {
            if (attr.HasExplicitValue &&
                safeStart >= attr.ValueStartOffset &&
                safeEnd <= attr.ValueEndOffset)
            {
                var attrPath = $"{elementXPath}/@{attr.Name}";
                var attrCss = $"{elementCss}::attr({attr.Name})";
                return new HtmlSelectionContext
                {
                    Element = best,
                    Attribute = attr,
                    HtmlPath = attrPath,
                    CssSelector = attrCss
                };
            }
        }

        return new HtmlSelectionContext
        {
            Element = best,
            Attribute = null,
            HtmlPath = elementXPath,
            CssSelector = elementCss
        };
    }

    public static XDocument? ToXDocument(string html)
    {
        var parsed = Parse(html);
        if (parsed.Roots.Count == 0)
        {
            return null;
        }

        HtmlParsedElement? htmlRoot = null;
        foreach (var root in parsed.Roots)
        {
            if (root.TagName.Equals("html", StringComparison.OrdinalIgnoreCase))
            {
                htmlRoot = root;
                break;
            }
        }

        XElement rootXElement;
        if (htmlRoot is not null)
        {
            rootXElement = BuildXElement(html, htmlRoot);
        }
        else if (parsed.Roots.Count == 1)
        {
            rootXElement = BuildXElement(html, parsed.Roots[0]);
        }
        else
        {
            rootXElement = new XElement("document");
            foreach (var root in parsed.Roots)
            {
                rootXElement.Add(BuildXElement(html, root));
            }
        }

        return new XDocument(rootXElement);
    }

    public static string BuildHtmlPath(HtmlParsedElement element, HtmlParseResult parsed)
    {
        var id = element.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id) &&
            parsed.IdCounts.TryGetValue(id, out var idCount) &&
            idCount == 1)
        {
            return $"//{element.TagName}[@id={QuoteXPathLiteral(id)}]";
        }

        if (element.TagName.Equals("meta", StringComparison.OrdinalIgnoreCase))
        {
            var prop = element.GetAttribute("property");
            if (!string.IsNullOrWhiteSpace(prop) &&
                parsed.MetaPropertyCounts.TryGetValue(prop, out var propCount) &&
                propCount == 1)
            {
                return $"//meta[@property={QuoteXPathLiteral(prop)}]";
            }

            var metaName = element.GetAttribute("name");
            if (!string.IsNullOrWhiteSpace(metaName) &&
                parsed.MetaNameCounts.TryGetValue(metaName, out var nameCount) &&
                nameCount == 1)
            {
                return $"//meta[@name={QuoteXPathLiteral(metaName)}]";
            }
        }

        if (element.TagName.Equals("link", StringComparison.OrdinalIgnoreCase))
        {
            var rel = element.GetAttribute("rel");
            if (!string.IsNullOrWhiteSpace(rel) &&
                parsed.LinkRelCounts.TryGetValue(rel, out var relCount) &&
                relCount == 1)
            {
                return $"//link[@rel={QuoteXPathLiteral(rel)}]";
            }
        }

        var segments = new List<string>();
        var current = element;
        while (current is not null)
        {
            if (current != element)
            {
                var ancestorId = current.GetAttribute("id");
                if (!string.IsNullOrWhiteSpace(ancestorId) &&
                    parsed.IdCounts.TryGetValue(ancestorId, out var ancIdCount) &&
                    ancIdCount == 1)
                {
                    segments.Add($"//{current.TagName}[@id={QuoteXPathLiteral(ancestorId)}]");
                    segments.Reverse();
                    return string.Join('/', segments);
                }
            }

            var siblings = current.Parent?.Children ?? parsed.Roots;
            var sameTagCount = 0;
            var sameTagIndex = 0;
            foreach (var sib in siblings)
            {
                if (sib.TagName.Equals(current.TagName, StringComparison.Ordinal))
                {
                    sameTagCount++;
                    if (ReferenceEquals(sib, current))
                    {
                        sameTagIndex = sameTagCount;
                    }
                }
            }

            var seg = sameTagCount > 1
                ? $"{current.TagName}[{sameTagIndex}]"
                : current.TagName;
            segments.Add(seg);
            current = current.Parent;
        }

        segments.Reverse();
        return "/" + string.Join('/', segments);
    }

    public static string BuildCssSelector(HtmlParsedElement element, HtmlParseResult parsed)
    {
        var id = element.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id) &&
            parsed.IdCounts.TryGetValue(id, out var idCount) &&
            idCount == 1)
        {
            return $"{element.TagName}#{id}";
        }

        var segments = new List<string>();
        var current = element;
        var steps = 0;
        while (current is not null && steps < 5)
        {
            var curId = current.GetAttribute("id");
            if (!string.IsNullOrWhiteSpace(curId) &&
                parsed.IdCounts.TryGetValue(curId, out var curIdCount) &&
                curIdCount == 1)
            {
                segments.Add($"{current.TagName}#{curId}");
                break;
            }

            var part = current.TagName;
            if (current.TagName is "meta")
            {
                var prop = current.GetAttribute("property");
                var name = current.GetAttribute("name");
                if (!string.IsNullOrWhiteSpace(prop))
                {
                    part = $"meta[property=\"{prop}\"]";
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    part = $"meta[name=\"{name}\"]";
                }
            }
            else if (current.TagName is "link")
            {
                var rel = current.GetAttribute("rel");
                if (!string.IsNullOrWhiteSpace(rel))
                {
                    part = $"link[rel=\"{rel}\"]";
                }
            }

            segments.Add(part);
            current = current.Parent;
            steps++;
        }

        segments.Reverse();
        return string.Join(" > ", segments);
    }

    private static ExtractorNode BuildExtractorNode(
        string html,
        HtmlParsedElement element,
        HtmlParseResult parsed,
        int depth)
    {
        var hasChildElements = element.Children.Count > 0;
        var scalarText = !hasChildElements
            ? ExtractInnerTextPreview(html, element)
            : string.Empty;

        var idAttr = element.GetAttribute("id");
        var nameAttr = element.GetAttribute("name");
        var propAttr = element.GetAttribute("property");
        var relAttr = element.GetAttribute("rel");

        var descriptor = !string.IsNullOrWhiteSpace(idAttr)
            ? $"#{Truncate(idAttr, 24)}"
            : !string.IsNullOrWhiteSpace(propAttr)
                ? $"[{propAttr}]"
                : !string.IsNullOrWhiteSpace(nameAttr)
                    ? $"[name={Truncate(nameAttr, 24)}]"
                    : string.Empty;

        var label = !hasChildElements && !string.IsNullOrEmpty(scalarText)
            ? $"<{element.TagName}{descriptor}>{Truncate(scalarText, PreviewMaxChars)}</{element.TagName}>"
            : $"<{element.TagName}{descriptor}>";

        var htmlPath = BuildHtmlPath(element, parsed);
        var cssSelector = BuildCssSelector(element, parsed);

        var node = new ExtractorNode
        {
            Label = label,
            KindTag = "html",
            NodeKind = ExtractorNodeKind.XmlElement,
            HtmlPath = htmlPath,
            CssSelector = cssSelector,
            ElementTagName = element.TagName,
            IdAttribute = idAttr,
            NameAttribute = nameAttr,
            PropertyAttribute = propAttr,
            RelAttribute = relAttr,
            RawScalarValue = scalarText,
            IsLeaf = !hasChildElements && element.Attributes.Count == 0,
            IsSelectable = !hasChildElements || element.Attributes.Count > 0 || !string.IsNullOrWhiteSpace(idAttr),
            IsExpanded = depth < 2
        };

        foreach (var attr in element.Attributes)
        {
            node.Children.Add(new ExtractorNode
            {
                Label = $"@{attr.Name} = \"{Truncate(attr.Value, PreviewMaxChars)}\"",
                KindTag = "attr",
                NodeKind = ExtractorNodeKind.XmlAttribute,
                HtmlPath = $"{htmlPath}/@{attr.Name}",
                CssSelector = $"{cssSelector}::attr({attr.Name})",
                ElementTagName = element.TagName,
                AttributeName = attr.Name,
                IdAttribute = idAttr,
                NameAttribute = nameAttr,
                PropertyAttribute = propAttr,
                RelAttribute = relAttr,
                RawScalarValue = attr.Value,
                IsLeaf = true,
                IsSelectable = true,
                IsExpanded = false
            });
        }

        foreach (var child in element.Children)
        {
            node.Children.Add(BuildExtractorNode(html, child, parsed, depth + 1));
        }

        return node;
    }

    private static XElement BuildXElement(string html, HtmlParsedElement element)
    {
        var safeName = SanitizeXmlName(element.TagName);
        var xEl = new XElement(safeName);

        foreach (var attr in element.Attributes)
        {
            var safeAttrName = SanitizeXmlName(attr.Name);
            if (xEl.Attribute(safeAttrName) is null)
            {
                xEl.SetAttributeValue(safeAttrName, StripInvalidXmlChars(attr.Value));
            }
        }

        if (element.Children.Count == 0)
        {
            var inner = ExtractRawInnerContent(html, element);
            if (!string.IsNullOrEmpty(inner))
            {
                if (!element.IsRawText)
                {
                    inner = StripHtmlComments(inner);
                    inner = WebUtility.HtmlDecode(inner);
                }

                xEl.Add(new XText(StripInvalidXmlChars(inner)));
            }
        }
        else
        {
            // Conserver le texte intercalé pour contains(., '...') et text()
            var cursor = element.OpenTagEndOffset;
            foreach (var child in element.Children)
            {
                if (child.StartOffset > cursor && cursor >= 0 && child.StartOffset <= html.Length)
                {
                    var textSlice = html[cursor..child.StartOffset];
                    var cleaned = StripHtmlComments(textSlice);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        xEl.Add(new XText(StripInvalidXmlChars(WebUtility.HtmlDecode(cleaned))));
                    }
                }

                xEl.Add(BuildXElement(html, child));
                cursor = Math.Max(cursor, child.EndOffset);
            }

            var closingLimit = FindInnerEndOffset(html, element);
            if (closingLimit > cursor && cursor >= 0 && closingLimit <= html.Length)
            {
                var trailingSlice = html[cursor..closingLimit];
                var cleaned = StripHtmlComments(trailingSlice);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    xEl.Add(new XText(StripInvalidXmlChars(WebUtility.HtmlDecode(cleaned))));
                }
            }
        }

        return xEl;
    }

    private static string ExtractInnerTextPreview(string html, HtmlParsedElement element)
    {
        var inner = ExtractRawInnerContent(html, element);
        if (string.IsNullOrWhiteSpace(inner))
        {
            return string.Empty;
        }

        if (!element.IsRawText)
        {
            inner = StripHtmlComments(inner);
            inner = WebUtility.HtmlDecode(inner);
        }

        var trimmed = inner.Trim();
        return trimmed.Length > 2048 ? trimmed[..2048] : trimmed;
    }

    private static string ExtractRawInnerContent(string html, HtmlParsedElement element)
    {
        if (element.IsVoidOrSelfClosing || element.OpenTagEndOffset >= element.EndOffset)
        {
            return string.Empty;
        }

        var innerEnd = FindInnerEndOffset(html, element);
        if (innerEnd <= element.OpenTagEndOffset)
        {
            return string.Empty;
        }

        return html[element.OpenTagEndOffset..innerEnd];
    }

    private static int FindInnerEndOffset(string html, HtmlParsedElement element)
    {
        if (element.EndOffset <= element.OpenTagEndOffset)
        {
            return element.OpenTagEndOffset;
        }

        var sliceStart = Math.Max(element.OpenTagEndOffset, element.EndOffset - element.TagName.Length - 16);
        var lastLt = html.LastIndexOf("</", element.EndOffset - 1, element.EndOffset - sliceStart, StringComparison.Ordinal);
        if (lastLt >= element.OpenTagEndOffset &&
            lastLt + 2 + element.TagName.Length <= element.EndOffset &&
            html.AsSpan(lastLt + 2, element.TagName.Length).Equals(element.TagName, StringComparison.OrdinalIgnoreCase))
        {
            return lastLt;
        }

        return element.EndOffset;
    }

    private static List<HtmlParsedAttribute> ParseAttributes(
        string html,
        int startPos,
        out int openTagGt,
        out bool selfClosing)
    {
        var attrs = new List<HtmlParsedAttribute>();
        var len = html.Length;
        var pos = startPos;
        selfClosing = false;
        openTagGt = -1;

        while (pos < len)
        {
            while (pos < len && char.IsWhiteSpace(html[pos]))
            {
                pos++;
            }

            if (pos >= len)
            {
                break;
            }

            var ch = html[pos];
            if (ch == '>')
            {
                openTagGt = pos;
                break;
            }

            if (ch == '/' && pos + 1 < len && html[pos + 1] == '>')
            {
                selfClosing = true;
                openTagGt = pos + 1;
                break;
            }

            if (ch == '<')
            {
                // Balise mal fermée avant la suivante
                openTagGt = pos - 1;
                break;
            }

            var nameStart = pos;
            while (pos < len && !char.IsWhiteSpace(html[pos]) && html[pos] != '=' && html[pos] != '>' && html[pos] != '<')
            {
                if (html[pos] == '/' && pos + 1 < len && html[pos + 1] == '>')
                {
                    break;
                }
                pos++;
            }

            if (pos == nameStart)
            {
                pos++;
                continue;
            }

            var attrName = html[nameStart..pos];

            while (pos < len && char.IsWhiteSpace(html[pos]))
            {
                pos++;
            }

            if (pos >= len || html[pos] != '=')
            {
                attrs.Add(new HtmlParsedAttribute
                {
                    Name = attrName,
                    Value = string.Empty,
                    NameStartOffset = nameStart,
                    ValueStartOffset = pos,
                    ValueEndOffset = pos,
                    QuoteChar = null,
                    HasExplicitValue = false
                });
                continue;
            }

            pos++; // skip '='
            while (pos < len && char.IsWhiteSpace(html[pos]))
            {
                pos++;
            }

            if (pos >= len)
            {
                break;
            }

            var valStart = pos;
            char? quote = null;
            string attrValue;
            int valEnd;

            if (html[pos] == '"' || html[pos] == '\'')
            {
                quote = html[pos];
                pos++;
                valStart = pos;
                var closeQuote = html.IndexOf(quote.Value, pos);
                if (closeQuote < 0)
                {
                    valEnd = len;
                    pos = len;
                }
                else
                {
                    valEnd = closeQuote;
                    pos = closeQuote + 1;
                }

                attrValue = html[valStart..valEnd];
            }
            else
            {
                while (pos < len && !char.IsWhiteSpace(html[pos]) && html[pos] != '>')
                {
                    if (html[pos] == '/' && pos + 1 < len && html[pos + 1] == '>')
                    {
                        break;
                    }
                    pos++;
                }

                valEnd = pos;
                attrValue = html[valStart..valEnd];
            }

            attrs.Add(new HtmlParsedAttribute
            {
                Name = attrName,
                Value = attrValue,
                NameStartOffset = nameStart,
                ValueStartOffset = valStart,
                ValueEndOffset = valEnd,
                QuoteChar = quote,
                HasExplicitValue = true
            });
        }

        return attrs;
    }

    private static int FindRawTextClosingTag(string html, int startOffset, string tagName, out int endOffset)
    {
        var len = html.Length;
        var pos = startOffset;

        while (pos < len)
        {
            var idx = html.IndexOf("</", pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                endOffset = len;
                return -1;
            }

            var nameStart = idx + 2;
            if (nameStart + tagName.Length <= len &&
                html.AsSpan(nameStart, tagName.Length).Equals(tagName, StringComparison.OrdinalIgnoreCase))
            {
                var afterName = nameStart + tagName.Length;
                while (afterName < len && char.IsWhiteSpace(html[afterName]))
                {
                    afterName++;
                }

                if (afterName < len && html[afterName] == '>')
                {
                    endOffset = afterName + 1;
                    return idx;
                }
            }

            pos = idx + 2;
        }

        endOffset = len;
        return -1;
    }

    private static void CloseMatchingTagOnStack(
        List<HtmlParsedElement> stack,
        string closeTagName,
        int closeTagStartOffset,
        int closeTagEndOffset)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].TagName.Equals(closeTagName, StringComparison.OrdinalIgnoreCase))
            {
                for (var j = stack.Count - 1; j > i; j--)
                {
                    stack[j].EndOffset = closeTagStartOffset;
                    stack.RemoveAt(j);
                }

                stack[i].EndOffset = closeTagEndOffset;
                stack.RemoveAt(i);
                return;
            }
        }
    }

    private static void AutoCloseImplicitTags(List<HtmlParsedElement> stack, string newTagName, int offset)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var top = stack[^1];
        if (top.TagName.Equals("p", StringComparison.Ordinal) && BlockTagsClosingP.Contains(newTagName))
        {
            top.EndOffset = offset;
            stack.RemoveAt(stack.Count - 1);
            return;
        }

        if (top.TagName.Equals("li", StringComparison.Ordinal) && newTagName.Equals("li", StringComparison.Ordinal))
        {
            top.EndOffset = offset;
            stack.RemoveAt(stack.Count - 1);
            return;
        }

        if (top.TagName.Equals("option", StringComparison.Ordinal) &&
            (newTagName.Equals("option", StringComparison.Ordinal) || newTagName.Equals("optgroup", StringComparison.Ordinal)))
        {
            top.EndOffset = offset;
            stack.RemoveAt(stack.Count - 1);
            return;
        }

        if ((top.TagName.Equals("td", StringComparison.Ordinal) || top.TagName.Equals("th", StringComparison.Ordinal)) &&
            (newTagName.Equals("td", StringComparison.Ordinal) || newTagName.Equals("th", StringComparison.Ordinal) || newTagName.Equals("tr", StringComparison.Ordinal)))
        {
            top.EndOffset = offset;
            stack.RemoveAt(stack.Count - 1);
            return;
        }

        if (top.TagName.Equals("tr", StringComparison.Ordinal) &&
            (newTagName.Equals("tr", StringComparison.Ordinal) ||
             newTagName.Equals("thead", StringComparison.Ordinal) ||
             newTagName.Equals("tbody", StringComparison.Ordinal) ||
             newTagName.Equals("tfoot", StringComparison.Ordinal)))
        {
            top.EndOffset = offset;
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static void RegisterElementStats(
        HtmlParsedElement element,
        Dictionary<string, int> idCounts,
        Dictionary<string, int> metaPropertyCounts,
        Dictionary<string, int> metaNameCounts,
        Dictionary<string, int> linkRelCounts)
    {
        var id = element.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            idCounts[id] = idCounts.GetValueOrDefault(id) + 1;
        }

        if (element.TagName.Equals("meta", StringComparison.Ordinal))
        {
            var prop = element.GetAttribute("property");
            if (!string.IsNullOrWhiteSpace(prop))
            {
                metaPropertyCounts[prop] = metaPropertyCounts.GetValueOrDefault(prop) + 1;
            }

            var metaName = element.GetAttribute("name");
            if (!string.IsNullOrWhiteSpace(metaName))
            {
                metaNameCounts[metaName] = metaNameCounts.GetValueOrDefault(metaName) + 1;
            }
        }
        else if (element.TagName.Equals("link", StringComparison.Ordinal))
        {
            var rel = element.GetAttribute("rel");
            if (!string.IsNullOrWhiteSpace(rel))
            {
                linkRelCounts[rel] = linkRelCounts.GetValueOrDefault(rel) + 1;
            }
        }
    }

    private static string QuoteXPathLiteral(string value)
    {
        if (!value.Contains('\''))
        {
            return $"'{value}'";
        }

        if (!value.Contains('"'))
        {
            return $"\"{value}\"";
        }

        var parts = value.Split('\'');
        return "concat('" + string.Join("', \"'\", '", parts) + "')";
    }

    private static string SanitizeXmlName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "node";
        }

        var clean = rawName.Replace(':', '_');
        try
        {
            return XmlConvert.VerifyNCName(clean);
        }
        catch (XmlException)
        {
            var sb = new StringBuilder(clean.Length);
            for (var i = 0; i < clean.Length; i++)
            {
                var ch = clean[i];
                if (i == 0)
                {
                    sb.Append(char.IsLetter(ch) || ch == '_' ? ch : '_');
                }
                else
                {
                    sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
                }
            }
            return sb.Length > 0 ? sb.ToString() : "node";
        }
    }

    private static string StripInvalidXmlChars(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (!XmlConvert.IsXmlChar(ch))
            {
                sb ??= new StringBuilder(value, 0, i, value.Length);
            }
            else if (sb is not null)
            {
                sb.Append(ch);
            }
        }

        return sb is null ? value : sb.ToString();
    }

    private static string StripHtmlComments(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("<!--", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var pos = 0;
        while (pos < text.Length)
        {
            var start = text.IndexOf("<!--", pos, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }

            sb.Append(text, pos, start - pos);
            var end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            pos = end + 3;
        }

        return sb.ToString();
    }

    private static bool IsTagNameStartChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool IsTagNameChar(char ch) =>
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= 'a' && ch <= 'z') ||
        (ch >= '0' && ch <= '9') ||
        ch is '-' or '_' or ':' or '.';

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var singleLine = value.Replace("\r\n", " ", StringComparison.Ordinal)
                              .Replace('\n', ' ')
                              .Replace('\r', ' ');
        return singleLine.Length <= maxChars
            ? singleLine
            : $"{singleLine[..(maxChars - 1)]}…";
    }
}
