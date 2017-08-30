﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Sgml;

namespace Xamarin.Android.Tools.JavadocImporterNG
{

	class HtmlLoader
	{
		// okay, SgmlReader itself has something similar, but I need something that makes sure to resolve only embedded resources.
		class EmbeddedResourceEntityResolver : Sgml.IEntityResolver
		{
			public IEntityContent GetContent (Uri uri)
			{
				return new EmbeddedResourceEntityContent (uri.LocalPath);
			}

			class EmbeddedResourceEntityContent : Sgml.IEntityContent
			{
				public EmbeddedResourceEntityContent (string name)
				{
					if (name == null)
						throw new ArgumentNullException (nameof (name));
					this.name = name;
				}

				string name;

				public Encoding Encoding {
					get { return Encoding.UTF8; }
				}

				public string MimeType {
					get { return "text/plain"; }
				}

				public Uri Redirect {
					get { return new Uri (this.name, UriKind.Relative); }
				}

				public Stream Open ()
				{
					return typeof (HtmlLoader).Assembly.GetManifestResourceStream (name);
				}
			}
		}

		enum JavaDocKind
		{
			DroidDoc,
			DroidDoc2,
			Java6,
			Java7,
			Java8
		}

		Sgml.SgmlDtd HtmlDtd;

		public HtmlLoader ()
		{
			HtmlDtd = Sgml.SgmlDtd.Parse (new Uri ("urn:anything"),
						      "HTML",
						      "-//W3C//DTD HTML 4.01//EN",
						      "strict.dtd",
						      string.Empty, new NameTable (), new EmbeddedResourceEntityResolver ());
		}

		public XElement GetJavaDocFile (string path)
		{
			JavaDocKind kind;
			return GetJavaDocFile (path, out kind);
		}

		XElement GetJavaDocFile (string path, out JavaDocKind kind)
		{
			kind = JavaDocKind.DroidDoc;
			string rawHTML = ReadHtmlFile (path);
			if (rawHTML.Substring (0, 3000).IndexOf ("<body class=\"gc-documentation develop reference api ", StringComparison.Ordinal) > 0)
				kind = JavaDocKind.DroidDoc2;
			if (rawHTML.Substring (0, 500).IndexOf ("Generated by javadoc (build 1.6", StringComparison.Ordinal) > 0)
				kind = JavaDocKind.Java6;
			if (rawHTML.Substring (0, 500).IndexOf ("Generated by javadoc (version 1.7", StringComparison.Ordinal) > 0)
				kind = JavaDocKind.Java7;
			if (rawHTML.Substring (0, 500).IndexOf ("Generated by javadoc (1.8", StringComparison.Ordinal) > 0)
				kind = JavaDocKind.Java8;
			if (kind == JavaDocKind.DroidDoc) {
				throw new NotSupportedException ("Old DroidDoc is not supported anymore.");
			} else {
				var html = new Sgml.SgmlReader () {
					InputStream = new StringReader (rawHTML),
					CaseFolding = Sgml.CaseFolding.ToLower,
					Dtd = HtmlDtd
				};
				var doc = XDocument.Load (html, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri);

				return doc.Root;
			}
		}

		string ReadHtmlFile (string path)
		{
			var info = new FileInfo (path);
			var contents = new StringBuilder (checked((int)info.Length));
			bool veryFirstChar = true;
			int wasPI = 0;
			using (var r = info.OpenText ()) {

				int ch;
				while ((ch = r.Read ()) >= 0) {
					// Some of the HTML files are invalid, containing constructs such as
					// 'foo <0', which should be 'foo &lt;0'...
					//
					// There are also <?> which needs to be replaced by &lt;?>, but this involves 3 chars so I replace it later.
					int next;
					if (wasPI == 2) {
						wasPI = 0;
						contents.Append ("&gt;");
					} else if (wasPI == 1) {
						if (r.Peek () == '>')
							wasPI = 2;
						else
							wasPI = 0;
						contents.Append ('?');
					} else if (ch == '<' && (next = r.Peek ()) >= 0 &&
						 (char.IsDigit ((char)next) || ((char)next) == '=' || ((char)next) == '.' || (char)next == '-' || (char)next == '?' && !veryFirstChar)) {
						contents.Append ("&lt;");
						if (next == '?')
							wasPI = 1;
					} else if (ch == '&') {
						var b = new List<char> ();
						while ((ch = r.Read ()) >= 0 && ch != ';' && ch != ' ') // sometimes the input HTML contains '&' as a standalone character (i.e. Google emits invalid HTML... android/support/test/espresso/idling/CountingIdlingResource.html has that problem.)
							b.Add ((char)ch);
						var entity = new string (b.ToArray ());
						switch (entity) {
						case "#124":
							contents.Append ("|");
							break;
						case "#160":
						case "#xA0":
						case "nbsp":
							contents.Append ("\u00A0");
							break;
						case "8211":
							contents.Append ("\u2011");
							break;
						default:
							contents.Append ("&").Append (entity).Append (";");
							break;
						}
					} else if (ch == '\0')
						contents.Append ("NUL");
					else
						contents.Append ((char)ch);
					if (veryFirstChar)
						veryFirstChar = false;
				}
			}

			var firstPass = contents.ToString ();
			File.WriteAllText ("/home/atsushi/Desktop/tmp.html", firstPass);
			contents.Clear ();
			int open_count = 0;
			bool in_quot = false, in_apos = false;
			foreach (char ch in firstPass) {
				if (ch == '"' && open_count > 0) {
					if (in_quot)
						open_count = 1; // reset. Something like <... ...="...<..." (without '>') happened
					in_quot = !in_quot;
				}
				if (ch == '\'' && open_count > 0) {
					if (in_quot)
						open_count = 1; // reset. Something like <... ...='...<...' (without '>') happened
					in_apos = !in_apos;
				}

				if (ch == '<') {
					if (open_count > 0)
						contents.Append ("&lt;");
					else
						contents.Append ((char)ch);
					open_count++;
				} else if (ch == '>') {
					if (open_count > 0)
						open_count--;
					if (open_count > 0)
						contents.Append ("&gt;");
					else
						contents.Append ((char)ch);
				} else
					contents.Append ((char)ch);
			}

			// FIXME: this should not be required, but Java.Util.Concurrent.ThreadPoolExecutor fails by invalid PI.
			return contents.Replace ("<?>", "&lt;?&gt;").ToString ();
		}
	}
}
