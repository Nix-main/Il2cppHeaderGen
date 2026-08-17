using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TreeSitter;

var dataRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
var inputPath = Path.Combine(dataRoot, "res", "il2cpp.h");
var outputPath = Path.Combine(dataRoot, "out", "il2cpp.h");

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"input header not found: {inputPath}");
    return 1;
}

using var language = new Language("C++");
using var parser = new Parser(language);
var il2CppHeaderData = File.ReadAllText(inputPath);
using var tree = parser.Parse(il2CppHeaderData)!;

var structsByName = new Dictionary<string, Node>();
IndexTypes(tree.RootNode);

void IndexTypes(Node node)
{
    if (node.Type is "struct_specifier" or "class_specifier")
    {
        var body = GetBody(node);
        var name = node.NamedChildren.FirstOrDefault(c => c.Type == "type_identifier");
        if (name is not null && body is not null)
            structsByName[name.Text] = node;
    }

    foreach (var child in node.NamedChildren)
        IndexTypes(child);
}

Node? GetBody(Node node)
{
    return node.NamedChildren.FirstOrDefault(c => c.Type == "field_declaration_list");
}

Node? FindIdentifier(Node node)
{
    if (node.Type is "field_identifier") return node;
    return node.NamedChildren.Select(FindIdentifier).FirstOrDefault(n => n is not null);
}

const string listPrefix = "System_Collections_Generic_List_";
const string dictPrefix = "System_Collections_Generic_Dictionary_";

string[] nestedDictTypes = ["Entry", "Enumerator", "KeyCollection", "ValueCollection"];

var primitiveAliases = new Dictionary<string, string>
{
    ["bool"] = "System_Boolean", ["byte"] = "System_Byte", ["sbyte"] = "System_SByte",
    ["char"] = "System_Char", ["short"] = "System_Int16", ["ushort"] = "System_UInt16",
    ["int"] = "System_Int32", ["uint"] = "System_UInt32", ["long"] = "System_Int64",
    ["ulong"] = "System_UInt64", ["float"] = "System_Single", ["double"] = "System_Double",
    ["decimal"] = "System_Decimal", ["string"] = "System_String", ["object"] = "System_Object"
};

string CanonicalTypeName(string arg)
{
    return primitiveAliases.GetValueOrDefault(arg, arg);
}

var unresolvedTypes = new HashSet<string>();

Node? StructBodyByName(string name)
{
    if (structsByName.TryGetValue(name, out var s))
    {
        return GetBody(s);
    }
    foreach (var key in structsByName.Keys)
    {
        if (key.EndsWith(name))
            return GetBody(structsByName[key]);
    }

    return null;
}

bool HasKlass(Node body)
{
    return body.NamedChildren.Any(c => FindIdentifier(c)?.Text == "klass");
}

string FlagUnresolvedType(string type)
{
    if (unresolvedTypes.Add(type)) Console.WriteLine($"could not resolve {type}");
    return type;
}

string SubstitutedType(string type)
{
    var name = type.StartsWith("struct ") ? type["struct ".Length..] : type;

    if (name.StartsWith(dictPrefix) && name.EndsWith("__o"))
    {
        var args = name[dictPrefix.Length..^"__o".Length];
        if (nestedDictTypes.Any(n => args.StartsWith(n + "_") && !args.StartsWith(n + "__")))
            return type;

        var parts = args.Split("__");
        if (parts.Length != 2) return FlagUnresolvedType(type);

        string key = CanonicalTypeName(parts[0]), value = CanonicalTypeName(parts[1]);
        if (StructBodyByName(key + "_o") is null || StructBodyByName(value + "_o") is null)
            return FlagUnresolvedType(type);
        return $"Dictionary<{key}, {value}>";
    }

    if (name.StartsWith(listPrefix) && name.EndsWith("__o"))
    {
        var elem = CanonicalTypeName(name[listPrefix.Length..^"__o".Length]);
        if (StructBodyByName(elem + "_o") is not { } body) return FlagUnresolvedType(type);
        return $"{(HasKlass(body) ? "Reference" : "Value")}List<{elem}>";
    }

    if (name.EndsWith("_array"))
    {
        var elem = CanonicalTypeName(name[..^"_array".Length]);
        if (StructBodyByName(elem + "_o") is not { } body) return FlagUnresolvedType(type);
        return $"{(HasKlass(body) ? "Reference" : "Value")}Array<{elem}>";
    }

    return type;
}

void AppendFields(StringBuilder target, Node body, bool skipVoid)
{
    foreach (var field in body.NamedChildren.Where(c => c.Type == "field_declaration"))
    {
        var id = FindIdentifier(field);
        var declaredType = field.NamedChildren.FirstOrDefault(c =>
            c.Type is "type_identifier" or "primitive_type" or "struct_specifier");
        if (id is null || declaredType is null) continue;

        var type = SubstitutedType(declaredType.Text);
        if (skipVoid && type == "void") continue;

        var isPointer = field.Text.Replace(declaredType.Text, "").Replace(id.Text, "").Contains('*');
        target.Append($"\t{type.Replace("_o", "")}{(isPointer ? "*" : "")} {id.Text};\n");
    }
}

var rex = new Regex("^(?!\\d+$).+");

string MergedStruct(string a)
{
    var builder = new StringBuilder();
    var body = GetBody(structsByName[a + "_o"]);
    if (body is null)
    {
        body = GetBody(structsByName[a]);
        if (body is null) return "";
    }

    var fields = GetBody(structsByName[a + "_Fields"]);
    if (fields is null) return "";
    var declaration = structsByName[a + "_Fields"].Text;
    var bodyStart = declaration.IndexOf('{');
    var head = bodyStart < 0 ? declaration : declaration[..bodyStart];

    var extra = "";
    if (head.IndexOf(':') is var colon && colon >= 0)
        extra = $" : {head[(colon + 1)..].Trim().Replace("_Fields", "").Split('_')[^1]}";
    var f = a.Split('_')[^1];
    if (!rex.IsMatch(f))
        return "";

    var hasStatics = structsByName.ContainsKey(f + "_StaticFields");
    var statics = new StringBuilder();
    if (hasStatics)
    {
        statics.Append($"struct {f}_c {{\n");
        statics.Append("\tchar pad[184];\n");
        statics.Append($"\tstruct {f}_StaticFields* static_fields;\n");
        statics.Append("};\n\n");
        statics.Append($"struct {f}_StaticFields {{\n");
        var body3 = GetBody(structsByName[f + "_StaticFields"]);
        if (body3 is null) return "";

        AppendFields(statics, body3, true);
        statics.Append("};\n\n");
    }

    var b = hasStatics ? f + "_c" : "void*";
    builder.Append($"struct {f}{extra} {{\n");
    builder.Append($"\t{b} klass;\n");
    builder.Append("\tvoid* monitor;\n");
    AppendFields(builder, fields, false);

    builder.Append("};\n\n");
    builder.Append(statics);
    return builder.ToString();
}

string[] strip =
[
    "System_Collections_Generic_List_",
    "System_Collections_Generic_Dictionary_",
    "MethodInfo",
    "_array",
    "XboxOne",
    "Ps4",
    "Ps5",
    "Android"
];

var header = new StringBuilder();
foreach (var key in structsByName.Keys)
{
    var v = new string(key.SkipLast(2).ToArray());
    if (!key.EndsWith("_o") || strip.Any(s => v.StartsWith(s) || v.EndsWith(s)))
        continue;
    header.Append(MergedStruct(v));
}

await using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Il2cppHeaderGen.add.h");
using var reader = new StreamReader(st!);
var content = await reader.ReadToEndAsync();
header.Append(content);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, header.ToString());

// all types we want to generate .h/.sym files for
List<string> types =
[
    "HeroController"
];

var scriptJson = Path.Combine(dataRoot, "res", "script.json");
if (File.Exists(scriptJson)) await SymbolExport.Run(scriptJson, Path.GetDirectoryName(outputPath)!, types);

return 0;