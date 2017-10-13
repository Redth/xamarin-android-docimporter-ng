﻿using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Xamarin.Android.Tools.ApiXmlAdjuster;

namespace Xamarin.Android.Tools.JavadocImporterNG
{
	public class DroidDocImporter
	{
		static bool ClassContains (XElement e, string cls)
		{
			return e.Attribute ("class")?.Value?.Split (' ')?.Contains (cls) == true;
		}

		static readonly string [] excludes = new string [] {
			"classes.html",
			"hierarchy.html",
			"index.html",
			"package-summary.html",
			"packages-wearable-support.html",
			"packages-wearable-support.html",
		};
		static readonly string [] non_frameworks = new string [] {
			"android.support.",
			"com.google.android.gms.",
			"renderscript."
		};

		/*

		The DroidDoc format from API Level 16 to 23, the format is:

		- All pages have ToC links and body (unlike standard JavaDoc which is based on HTML frames).
		- The actual doc section is a div element whose id is "doc-col".
		- The "doc-col" div element has a section div element whose id is "jd-header" and another one with id "jd-content".
		- "jd-header" div element contains the type signature (modifiers, name, and inheritance).
		  - Here we care only about type name and kind (whether it is a class or interface).
		    - Generic arguments are insignificant.
		- "jd-content" div element contains a collection of sections. Each section consists of:
		  - an "h2" element whose value text indicates the section name ("Public Constructors", "Protected Methods" etc.), and
		  - the content, which follows the h2 element.
		- The section content is a collection of members. Each member consists of:
		  - an anchor ("A") element with "name" attribute, and
		  - a div element which contains an h4 child element whose class contains "jd-details-title".
		- The h4 element contains the member signature. We parse it and retrieve the method name and list of parameters.
		  - Parameters are tokenized by ", ".
		  - Note that the splitter contains a white space which disambiguates any use of generic arguments (we don't want to split "Foo<K,V> bar" as "Foo<K" and "V> bar")

		API Level 10 to 15 has slightly different format:

		- There is no "doc-col" element. But "jd-header" and "jd-content" are still alive.

		*/
		public void Import (ImporterOptions options)
		{
			options.DiagnosticWriter.WriteLine (options.DocumentDirectory);

			string referenceDocsTopDir = Path.Combine (options.DocumentDirectory, "reference");
			var htmlFiles = Directory.GetDirectories (referenceDocsTopDir).SelectMany (d => Directory.GetFiles (d, "*.html", SearchOption.AllDirectories));

			var api = new JavaApi ();

			foreach (var htmlFile in htmlFiles) {

				// skip irrelevant files.
				if (excludes.Any (x => htmlFile.EndsWith (x, StringComparison.OrdinalIgnoreCase)))
					continue;
				var packageName = Path.GetDirectoryName (htmlFile).Substring (referenceDocsTopDir.Length + 1).Replace ('/', '.');
				if (options.FrameworkOnly && non_frameworks.Any (n => packageName.StartsWith (n, StringComparison.Ordinal)))
					continue;

				options.DiagnosticWriter.WriteLine ("-- " + htmlFile);

				var doc = new HtmlLoader ().GetJavaDocFile (htmlFile);

				var header = doc.Descendants ().FirstOrDefault (e => e.Attribute ("id")?.Value == "jd-header");
				var content = doc.Descendants ().FirstOrDefault (e => e.Attribute ("id")?.Value == "jd-content");

				if (header == null || content == null)
					continue;

				var apiSignatureTokens = header.Value.Replace ('\r', ' ').Replace ('\n', ' ').Replace ('\t', ' ').Trim ();
				if (apiSignatureTokens.Contains ("extends "))
					apiSignatureTokens = apiSignatureTokens.Substring (0, apiSignatureTokens.IndexOf ("extends ", StringComparison.Ordinal)).Trim ();
				if (apiSignatureTokens.Contains ("implements "))
					apiSignatureTokens = apiSignatureTokens.Substring (0, apiSignatureTokens.IndexOf ("implements ", StringComparison.Ordinal)).Trim ();
				bool isClass = apiSignatureTokens.Contains ("class");
				options.DiagnosticWriter.WriteLine (apiSignatureTokens);

				var javaPackage = api.Packages.FirstOrDefault (p => p.Name == packageName);
				if (javaPackage == null) {
					javaPackage = new JavaPackage (api) { Name = packageName };
					api.Packages.Add (javaPackage);
				}

				var javaType = isClass ? (JavaType)new JavaClass (javaPackage) : new JavaInterface (javaPackage);
				javaType.Name = apiSignatureTokens.Substring (apiSignatureTokens.LastIndexOf (' ') + 1);
				javaPackage.Types.Add (javaType);

				string sectionType = null;
				var sep = new string [] { ", " };
				var ssep = new char [] { ' ' };
				foreach (var child in content.Elements ()) {
					if (child.Name == "h2") {
						sectionType = child.Value;
						continue;
					}
					switch (sectionType) {
					case "Public Constructors":
					case "Protected Constructors":
					case "Public Methods":
					case "Protected Methods":
						break;
					default:
						continue;
					}
					if (child.Name != "a" || child.Attribute ("name") == null)
						continue;

					var h4 = (child.XPathSelectElement ("following-sibling::div") as XElement)?.Elements ("h4")?.FirstOrDefault (e => ClassContains (e, "jd-details-title"));
					if (h4 == null)
						continue;

					string sig = h4.Value.Replace ('\n', ' ').Replace ('\r', ' ').Trim ();
					if (!sig.Contains ('('))
						continue;
					JavaMethodBase javaMethod = null;
					string name = sig.Substring (0, sig.IndexOf ('(')).Split (ssep, StringSplitOptions.RemoveEmptyEntries).Last ();
					switch (sectionType) {
					case "Public Constructors":
					case "Protected Constructors":
						javaMethod = new JavaConstructor (javaType) { Name = name };
						break;
					case "Public Methods":
					case "Protected Methods":
						string mname = sig.Substring (0, sig.IndexOf ('('));
						javaMethod = new JavaMethod (javaType) { Name = name };
						break;
					}
					javaType.Members.Add (javaMethod);

					var parameters = sig.Substring (sig.IndexOf ('(') + 1).TrimEnd (')')
							    .Split (sep, StringSplitOptions.RemoveEmptyEntries)
							    .Select (s => s.Trim ())
							    .ToArray ();
					foreach (var p in parameters.Select (pTN => pTN.Split (' ')))
						javaMethod.Parameters.Add (new JavaParameter (javaMethod) { Name = p [1], Type = p [0] });
				}
			}

			if (options.OutputFile != null) {
				if (options.ParameterNamesFormat == ParameterNamesFormat.SimpleText)
					api.WriteParameterNamesText (options.OutputFile);
				else
					api.WriteParameterNamesXml (options.OutputFile);
			}
		}
	}

	public class ImporterOptions
	{
		public string DocumentDirectory { get; set; }
		public string OutputFile { get; set; }
		public TextWriter DiagnosticWriter { get; set; }
		public ParameterNamesFormat ParameterNamesFormat { get; set; }
		public bool FrameworkOnly { get; set; }
	}
}
