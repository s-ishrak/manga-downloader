using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.XPath;
using HtmlAgilityPack;
using MoonSharp.Interpreter;

namespace MangaDownloader.Lua.Api
{
    /// <summary>XPath 1.0 evaluation over HtmlAgilityPack, shared by documents and nodes.</summary>
    internal static class HtmlQuery
    {
        public static string NodeValue(XPathNavigator nav) => HtmlEntity.DeEntitize(nav.Value);

        public static IEnumerable<XPathNavigator> Select(XPathNavigator root, string xpath)
        {
            var result = root.Evaluate(xpath);

            if (result is XPathNodeIterator iterator)
            {
                while (iterator.MoveNext())
                    yield return iterator.Current.Clone();
            }
        }

        public static string SelectValue(XPathNavigator root, string xpath)
        {
            var result = root.Evaluate(xpath);

            if (result is XPathNodeIterator iterator)
                return iterator.MoveNext() ? NodeValue(iterator.Current) : null;

            return result is double d ? LuaConv.Num(d) : result is bool b ? (b ? "true" : "false") : result?.ToString();
        }

        public static ListUserData SelectValues(XPathNavigator root, string xpath) =>
            new ListUserData(Select(root, xpath).Select(n => (object)NodeValue(n)));

        public static HtmlNode ToNode(XPathNavigator nav) => (nav as HtmlNodeNavigator)?.CurrentNode;

        public static HtmlNodeUserData SelectElement(XPathNavigator root, string xpath)
        {
            var nav = Select(root, xpath).FirstOrDefault();
            var node = nav == null ? null : ToNode(nav);
            return node == null ? null : new HtmlNodeUserData(node);
        }

        public static ListUserData SelectElements(XPathNavigator root, string xpath) =>
            new ListUserData(Select(root, xpath).Select(ToNode).Where(n => n != null).Select(n => (object)new HtmlNodeUserData(n)));
    }

    [MoonSharpUserData]
    public class HtmlDocumentUserData
    {
        private readonly HtmlDocument document;
        private readonly string html;
        private XPathNavigator navigator;

        public HtmlDocumentUserData(string html, string url)
        {
            this.html = html ?? "";
            document = new HtmlDocument();
            document.LoadHtml(this.html);
            Url = url;
        }

        private XPathNavigator Nav => navigator ?? (navigator = document.CreateNavigator());

        public static HtmlDocumentUserData New(string html) => new HtmlDocumentUserData(html, null);

        public static HtmlDocumentUserData New(string html, string url) => new HtmlDocumentUserData(html, url);

        public string Url { get; }

        public string BaseUri => Url;

        public string SelectValue(string xpath) => HtmlQuery.SelectValue(Nav, xpath);

        public ListUserData SelectValues(string xpath) => HtmlQuery.SelectValues(Nav, xpath);

        public HtmlNodeUserData SelectElement(string xpath) => HtmlQuery.SelectElement(Nav, xpath);

        public ListUserData SelectElements(string xpath) => HtmlQuery.SelectElements(Nav, xpath);

        public HtmlNodeUserData SelectNode(string xpath) => SelectElement(xpath);

        public ListUserData SelectNodes(string xpath) => SelectElements(xpath);

        public HtmlNodeUserData GetElementById(string id) => SelectElement("//*[@id=" + Literal(id) + "]");

        public ListUserData GetElementsByClassName(string name) =>
            SelectElements("//*[contains(concat(' ', normalize-space(@class), ' '), " + Literal(" " + name + " ") + ")]");

        public ListUserData GetElementsByTagName(string name) => SelectElements("//" + name);

        public string Title => SelectValue("//title");

        public HtmlNodeUserData Body => SelectElement("//body");

        public HtmlNodeUserData Head => SelectElement("//head");

        public HtmlNodeUserData DocumentElement => SelectElement("/*[1]");

        public ListUserData Links => SelectElements("//a[@href]");

        public ListUserData Anchors => SelectElements("//a");

        public ListUserData Images => SelectElements("//img");

        public ListUserData Scripts => SelectElements("//script");

        public ListUserData Forms => SelectElements("//form");

        public ListUserData Embeds => SelectElements("//embed");

        public string Doctype => null;

        public string InputEncoding => document.Encoding?.WebName;

        /// <summary>The page source as received (so <c>tostring(dom):regex(...)</c> sees the original script text).</summary>
        public override string ToString() => html;

        internal static string Literal(string s) => s.Contains("'") ? "\"" + s + "\"" : "'" + s + "'";
    }

    [MoonSharpUserData]
    public class HtmlNodeUserData
    {
        private readonly HtmlNode node;

        public HtmlNodeUserData(HtmlNode node)
        {
            this.node = node;
        }

        private XPathNavigator Nav => node.CreateNavigator();

        public string SelectValue(string xpath) => HtmlQuery.SelectValue(Nav, xpath);

        public ListUserData SelectValues(string xpath) => HtmlQuery.SelectValues(Nav, xpath);

        public HtmlNodeUserData SelectElement(string xpath) => HtmlQuery.SelectElement(Nav, xpath);

        public ListUserData SelectElements(string xpath) => HtmlQuery.SelectElements(Nav, xpath);

        public HtmlNodeUserData SelectNode(string xpath) => SelectElement(xpath);

        public ListUserData SelectNodes(string xpath) => SelectElements(xpath);

        public string GetAttribute(string name) => node.Attributes.Contains(name) ? HtmlEntity.DeEntitize(node.GetAttributeValue(name, "")) : null;

        public bool HasAttribute(string name) => node.Attributes.Contains(name);

        public string TagName => node.Name;

        public string Id => node.Id;

        public string ClassName => GetAttribute("class");

        public ListUserData ClassList => new ListUserData((ClassName ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Cast<object>());

        public string InnerHtml => node.InnerHtml;

        public string OuterHtml => node.OuterHtml;

        public string InnerText => HtmlEntity.DeEntitize(node.InnerText);

        public string TextContent => InnerText;

        public string Title => GetAttribute("title");

        public string Lang => GetAttribute("lang");

        public string Dir => GetAttribute("dir");

        public bool HasAttributes => node.HasAttributes;

        public bool HasChildNodes => node.HasChildNodes;

        public int ChildElementCount => node.ChildNodes.Count(n => n.NodeType == HtmlNodeType.Element);

        public ListUserData Children => new ListUserData(node.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).Select(n => (object)new HtmlNodeUserData(n)));

        public ListUserData ChildNodes => new ListUserData(node.ChildNodes.Select(n => (object)new HtmlNodeUserData(n)));

        public HtmlNodeUserData FirstChild => Wrap(node.FirstChild);

        public HtmlNodeUserData LastChild => Wrap(node.LastChild);

        public HtmlNodeUserData ParentNode => Wrap(node.ParentNode);

        public HtmlNodeUserData NextSibling => Wrap(node.NextSibling);

        public HtmlNodeUserData PreviousSibling => Wrap(node.PreviousSibling);

        /// <summary><c>node[0]</c> is the n-th child element; <c>node["href"]</c> is an attribute value.</summary>
        public object this[object key]
        {
            get
            {
                if (key is string name)
                    return GetAttribute(name);

                var index = LuaConv.ToInt(key);
                var children = node.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToList();
                return index >= 0 && index < children.Count ? new HtmlNodeUserData(children[index]) : null;
            }
        }

        private static HtmlNodeUserData Wrap(HtmlNode n) => n == null ? null : new HtmlNodeUserData(n);

        /// <summary>The node's text (so <c>tostring(node)</c> reads like the page text, as in the original).</summary>
        public override string ToString() => OuterHtml;
    }
}
