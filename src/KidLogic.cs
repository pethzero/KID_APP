using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KidHelper;

// ---------------------------------------------------------------------------
// Keyword presets  (the "array design" - add your own here)
// ---------------------------------------------------------------------------
public record KeywordPreset(string Name, string Plugin, string List, string? FilePath = null, string Type = "Weapon")
{
	public override string ToString() => Name;
}

public static class Presets
{
	public static readonly KeywordPreset[] All =
	{
		new("Smooth Weapon.esm", "Smooth Weapon.esm",
			";0x800\tWeap TypeRapier\n;0x801\tWeap TypePike\n;0x803\tWeap TypeHalberd\n;0x804\tWeap TypeQtrStaff\n" +
			";0x805\tWeap TypeClaw\n;0x806\tWeap TypeScythe\n;0x807\tWeap TypeKatana\n;0x809\tWeap TypeWhip\n" +
			";0x80A\tWeap TypeSpear\n;0x80B\tWeap TypeJavelin\n;0x80C\tWeap TypeTdagger\n;0x80D\tWeap TypeLance\n" +
			";0x80E\tWeap TypeArrow\n;0x810\tWeap TypeGun\n;0x815\tWeap TypeUW\n;0x824\tGiantGreatSword\n" +
			";0x82A\tWeap TypePistol\n;0x82B\tWeap TypeRifle\n;0x82C\tWeap TypeMinigun\n;0x82D\tWeap TypeLargeShield\n" +
			";0x82E\tGiantBattleAxe\n;0x82F\tGiantBattleHammer\n;0x830\tGiantKatana\n;0x831\tDelia\n;0x832\tLynn"),
		new("kcf.esm", "kcf.esm",
			";0x800\tSpikeWorkaround\n;0x801\tWeapTypeSpear\n;0x802\tWeapTypeJavelin\n;0x803\tWeapTypePike\n" +
			";0x804\tWeapTypeHalberd\n;0x805\tWeapTypeRapier\n;0x806\tWeapTypeQuarterstaff\n;0x807\tWeapTypeClaw\n" +
			";0x808\tWeapTypeScythe\n;0x809\tWeapTypeWhip\n;0x80A\tWeapTypeKatana\n;0x80B\tWeapTypeGun\n" +
			";0x80C\tWeapTypeTwinblade\n;0x80D\tWeapTypeCurvedSword\n;0x80E\tWeapTypeCurvedGreatsword\n;0x80F\tArmorShieldLarge"),
		new("Skyrim.esm (vanilla EditorIDs)", "Skyrim.esm",
			"WeapTypeSword\nWeapTypeGreatsword\nWeapTypeDagger\nWeapTypeWarAxe\nWeapTypeBattleaxe\nWeapTypeMace\n" +
			"WeapTypeWarhammer\nWeapTypeBow\nWeapTypeStaff\nArmorHeavy\nArmorLight\nArmorShield"),
		new("(Custom / empty)", "", ""),
	};
}

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------
public sealed class KeywordItem : INotifyPropertyChanged
{
	public string Id { get; init; } = "";
	public string Name { get; init; } = "";

	private bool _isSelected;
	public bool IsSelected
	{
		get => _isSelected;
		set { if (_isSelected != value) { _isSelected = value; OnChanged(); } }
	}

	public bool IsFormId => Id.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

	public event PropertyChangedEventHandler? PropertyChanged;
	private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}

public sealed record ParsedItem(string Input, string Filter, string Note, bool Ok);

public sealed class GenerateOptions
{
	public string KeywordPlugin = "";
	public string FormType = "Weapon";
	public string Traits = "";
	public string Chance = "";
	public bool Merge;
	public bool Comments = true;
	public bool Header;
}

// ---------------------------------------------------------------------------
// Logic  (FormID rules from "FormIDs and You")
//   Skyrim.esm             : 0001397E -> 0x1397E
//   ESP/ESM                : 04000914 -> keep last 6 -> 0x914~example.esp
//   ESL / ESL-flagged / FE : FE044801 -> keep last 3 -> 0x801~plugin.esl
// ---------------------------------------------------------------------------
public static class KidLogic
{
	// from KID EnumDefs.h header_strings
	public static readonly string[] FormTypes =
	{
		"Weapon", "Armor", "Ammo", "Magic Effect", "Potion", "Scroll", "Location", "Ingredient", "Book",
		"Misc Item", "Key", "Soul Gem", "Spell", "Activator", "Flora", "Furniture", "Race", "Talking Activator", "Enchantment"
	};

	private static readonly Regex ReKid = new(@"^0x[0-9A-Fa-f]+~.+$", RegexOptions.Compiled);
	private static readonly Regex ReHex = new(@"(^|[^0-9A-Za-z_])(?<tok>0[xX](?<loc>[0-9A-Fa-f]{1,8})|(?<full>[0-9A-Fa-f]{8}))(?![0-9A-Za-z_])", RegexOptions.Compiled);
	private static readonly Regex ReXeditTag = new(@"\[[A-Z_]{4}:\s*\]", RegexOptions.Compiled);
	private static readonly Regex ReKwLine = new(@"^(0[xX][0-9A-Fa-f]+)\s*(?:-\s*)?(.*)$", RegexOptions.Compiled);
	private static readonly Regex ReKwEditorId = new(@"^([A-Za-z_][A-Za-z0-9_]*)(?:\s*(?:\t|\s-\s|\s{2,})\s*(.*))?$", RegexOptions.Compiled);

	public static bool IsSkyrimEsm(string p) => p.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase);

	private static string StripZeros(string h)
	{
		var t = h.TrimStart('0');
		return t.Length == 0 ? "0" : t;
	}

	public static IEnumerable<string> Lines(string text) =>
		text.Replace("\r\n", "\n").Split('\n');

	/// <summary>Parse one item line. Returns null for empty / comment lines.</summary>
	public static ParsedItem? ParseItem(string raw, string defaultPlugin, bool forceEsl)
	{
		var line = raw.Trim().Trim('﻿');
		if (line.Length == 0 || line[0] == ';' || line[0] == '#') return null;

		if (ReKid.IsMatch(line)) return new(line, line, "already KID format", true);

		var ms = ReHex.Matches(line);
		if (ms.Count == 0) return new(line, line, "EditorID / string filter", true);

		var m = ms[^1];
		var tok = m.Groups["tok"];
		bool full = m.Groups["full"].Success;
		string hex = (full ? m.Groups["full"].Value : m.Groups["loc"].Value).ToUpperInvariant();

		string rest = line[..tok.Index] + " " + line[(tok.Index + tok.Length)..];
		rest = ReXeditTag.Replace(rest, " ");

		string plugin = FindPlugin(rest);
		if (plugin.Length == 0) plugin = defaultPlugin.Trim();
		if (plugin.Length == 0) {
			if (full && hex.StartsWith("00")) plugin = "Skyrim.esm";
			else return new(line, "", "missing plugin name — set Default plugin", false);
		}

		string local, how;
		if (full) {
			bool light = forceEsl || plugin.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) || hex.StartsWith("FE");
			local = light ? hex[5..] : hex[2..];
			how = light ? "ESL · last 3" : "ESP/ESM · last 6";
		} else {
			local = hex;
			how = "local ID";
		}
		local = StripZeros(local);

		string filter = IsSkyrimEsm(plugin) ? "0x" + local : $"0x{local}~{plugin}";
		return new(line, filter, $"{plugin}  ·  {how}", true);
	}

	private static string FindPlugin(string rest)
	{
		var lr = rest.ToLowerInvariant();
		int best = -1;
		foreach (var ext in new[] { ".esp", ".esm", ".esl" }) {
			int p = lr.LastIndexOf(ext, StringComparison.Ordinal);
			if (p > best) best = p;
		}
		if (best < 0) return "";
		int end = best + 4;
		int start = rest.LastIndexOfAny("\t|,;\"~".ToCharArray(), best);
		start = start < 0 ? 0 : start + 1;
		return rest[start..end].Trim().Trim('"');
	}

	public static List<KeywordItem> ParseKeywordList(string text)
	{
		var list = new List<KeywordItem>();
		foreach (var raw in Lines(text)) {
			var l = raw.Trim();
			while (l.StartsWith(';')) l = l[1..].Trim();
			if (l.Length == 0 || l.Trim('-', '=').Length == 0) continue;

			var m = ReKwLine.Match(l);
			if (m.Success) {
				list.Add(new KeywordItem { Id = "0x" + StripZeros(m.Groups[1].Value[2..]).ToUpperInvariant(), Name = m.Groups[2].Value.Trim() });
			} else if (ReKwEditorId.Match(l) is { Success: true } e) {
				// EditorID keyword:  WeapTypeSword  |  WeapTypeSword<TAB>Name  |  WeapTypeSword - Name
				list.Add(new KeywordItem { Id = e.Groups[1].Value, Name = e.Groups[2].Value.Trim() });
			}
			// anything else (free-text comment) is ignored
		}
		return list;
	}

	public static string KeywordString(KeywordItem k, string plugin)
	{
		if (!k.IsFormId) return k.Id;
		plugin = plugin.Trim();
		return plugin.Length == 0 || IsSkyrimEsm(plugin) ? k.Id : $"{k.Id}~{plugin}";
	}

	public static string BuildHeader(IEnumerable<KeywordItem> keywords, string plugin)
	{
		var sb = new StringBuilder(";-------------------\r\n");
		if (plugin.Trim().Length > 0) sb.Append(';').Append(plugin.Trim()).Append("\r\n");
		foreach (var k in keywords) sb.Append(';').Append(k.Id).Append('\t').Append(k.Name).Append("\r\n");
		return sb.ToString();
	}

	public static string Generate(IReadOnlyList<KeywordItem> allKeywords, IReadOnlyList<KeywordItem> selected,
		IReadOnlyList<ParsedItem> items, GenerateOptions o)
	{
		var sb = new StringBuilder();
		if (o.Header) sb.Append(BuildHeader(allKeywords, o.KeywordPlugin)).Append("\r\n");

		var errors = items.Where(i => !i.Ok).ToList();
		foreach (var e in errors) sb.Append("; ERROR: ").Append(e.Input).Append("  <- ").Append(e.Note).Append("\r\n");
		if (errors.Count > 0) sb.Append("\r\n");

		if (selected.Count == 0) {
			sb.Append("; ← เลือก Keyword ทางซ้ายก่อน (select keyword(s) on the left)\r\n");
			return sb.ToString();
		}

		var ok = items.Where(i => i.Ok).ToList();
		string tail = "";
		if (o.Traits.Trim().Length > 0 || o.Chance.Trim().Length > 0) tail += "|" + (o.Traits.Trim().Length > 0 ? o.Traits.Trim() : "NONE");
		if (o.Chance.Trim().Length > 0) tail += "|" + o.Chance.Trim();

		foreach (var k in selected) {
			string kw = KeywordString(k, o.KeywordPlugin);
			if (o.Comments) sb.Append(';').Append(k.Name.Length > 0 ? k.Name : k.Id).Append("\r\n");

			if (ok.Count == 0) {
				sb.Append($"Keyword = {kw}|{o.FormType}{tail}\r\n");
			} else if (o.Merge) {
				if (o.Comments) foreach (var i in ok) sb.Append(";  ").Append(i.Input).Append(" -> ").Append(i.Filter).Append("\r\n");
				sb.Append($"Keyword = {kw}|{o.FormType}|{string.Join(",", ok.Select(i => i.Filter))}{tail}\r\n");
			} else {
				foreach (var i in ok) {
					if (o.Comments && i.Input != i.Filter) sb.Append(";  ").Append(i.Input).Append("\r\n");
					sb.Append($"Keyword = {kw}|{o.FormType}|{i.Filter}{tail}\r\n");
				}
			}
			sb.Append("\r\n");
		}
		return sb.ToString();
	}
}
