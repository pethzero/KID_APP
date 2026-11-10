using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KidHelper;

/// <summary>
/// Loads keyword sets from  &lt;exe folder&gt;\keywords\*.txt
/// File format: see README.md ("Keyword file format").
/// </summary>
public static class PresetStore
{
	public static string Folder => Path.Combine(AppContext.BaseDirectory, "keywords");

	private static readonly Regex ReSetting = new(@"^(plugin|name|type)\s*[=:]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly Regex RePluginLine = new(@"^[^\t;=|]+\.(esm|esp|esl)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
	private static readonly UTF8Encoding Utf8NoBom = new(false);

	public static List<KeywordPreset> LoadAll()
	{
		EnsureDefaults();
		var list = new List<KeywordPreset>();
		foreach (var f in Directory.GetFiles(Folder, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
			try {
				var p = ParseFile(f);
				if (p.List.Length > 0) list.Add(p);
			} catch { /* unreadable file - skip */ }
		}
		list.Add(new KeywordPreset("(Custom / empty)", "", ""));
		return list;
	}

	public static KeywordPreset ParseFile(string path) => Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), path);

	public static KeywordPreset Parse(string text, string fallbackName, string? path = null)
	{
		string name = "", plugin = "", type = "Weapon";
		var entries = new List<string>();

		foreach (var raw in KidLogic.Lines(text)) {
			var line = raw.Trim().Trim('﻿');
			if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;   // comments
			var body = line.TrimStart(';').Trim();                                                // ';' is optional (ini header style)
			if (body.Length == 0 || body.Trim('-', '=', ' ').Length == 0) continue;               // ;------------

			var kv = ReSetting.Match(body);
			if (kv.Success) {
				var v = kv.Groups[2].Value.Trim();
				switch (kv.Groups[1].Value.ToLowerInvariant()) {
				case "plugin": plugin = v; break;
				case "name": name = v; break;
				case "type": type = v; break;
				}
				continue;
			}
			if (!body.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && RePluginLine.IsMatch(body)) {
				if (plugin.Length == 0) plugin = body;   // a bare "Smooth Weapon.esm" line = the keyword plugin
				continue;
			}
			entries.Add(body);
		}

		if (plugin.Length == 0 && RePluginLine.IsMatch(fallbackName)) plugin = fallbackName;
		if (name.Length == 0) name = plugin.Length > 0 ? plugin : fallbackName;
		return new KeywordPreset(name, plugin, string.Join("\n", entries), path, type);
	}

	public static string Serialize(string plugin, IEnumerable<KeywordItem> keywords, string type = "Weapon")
	{
		var sb = new StringBuilder();
		sb.Append("# KID_APP keyword file — see README.md\r\n");
		sb.Append("# plugin = the .esm/.esp/.esl that contains these KYWD records\r\n");
		sb.Append("plugin = ").Append(plugin).Append("\r\n");
		sb.Append("type = ").Append(type).Append("\r\n\r\n");
		foreach (var k in keywords) sb.Append(k.Id).Append('\t').Append(k.Name).Append("\r\n");
		return sb.ToString();
	}

	public static string Save(string plugin, IEnumerable<KeywordItem> keywords, string type = "Weapon")
	{
		Directory.CreateDirectory(Folder);
		var baseName = plugin.Trim().Length > 0 ? plugin.Trim() : "Custom";
		foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
		var path = Path.Combine(Folder, baseName + ".txt");
		File.WriteAllText(path, Serialize(plugin, keywords, type), Utf8NoBom);
		return path;
	}

	/// <summary>First run: write the built-in sets so the folder is never empty.</summary>
	private static void EnsureDefaults()
	{
		if (Directory.Exists(Folder) && Directory.GetFiles(Folder, "*.txt").Length > 0) return;
		Directory.CreateDirectory(Folder);
		foreach (var p in Presets.All.Where(p => p.List.Length > 0)) {
			var items = KidLogic.ParseKeywordList(p.List);
			var file = Path.Combine(Folder, p.Plugin + ".txt");
			if (!File.Exists(file)) File.WriteAllText(file, Serialize(p.Plugin, items, p.Type), Utf8NoBom);
		}
	}
}
