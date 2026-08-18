using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TreeSitter;

namespace Il2cppHeaderGen;

public class Program {

    public static string dataRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
    public static string inputPath = Path.Combine(dataRoot, "res", "il2cpp.h");
    public static string outputPath = Path.Combine(dataRoot, "out", "il2cpp.h");

    public static Dictionary<string, Node> structsByName = new();
    public static void IndexTypes(Node node, Dictionary<string, Node> structsByName)
    {
        if (node.Type is "struct_specifier" or "class_specifier")
        {
            var body = GetBody(node);
            var name = node.NamedChildren.FirstOrDefault(c => c.Type == "type_identifier");
            if (name is not null && body is not null)
                structsByName[name.Text] = node;
        }

        foreach (var child in node.NamedChildren)
            IndexTypes(child, structsByName);
    }

    public static Node? GetBody(Node? node)
    {
        return node.NamedChildren.FirstOrDefault(c => c.Type == "field_declaration_list");
    }

    public static Node? FindIdentifier(Node node)
    {
        if (node.Type is "field_identifier") return node;
        return node.NamedChildren.Select(FindIdentifier).FirstOrDefault(n => n is not null);
    }

    const string listPrefix = "System_Collections_Generic_List_";
    const string dictPrefix = "System_Collections_Generic_Dictionary_";

    public static string[] nestedDictTypes = ["Entry", "Enumerator", "KeyCollection", "ValueCollection"];

    public static Dictionary<string, string> primitiveAliases = new()
    {
        ["bool"] = "System_Boolean", ["byte"] = "System_Byte", ["sbyte"] = "System_SByte",
        ["char"] = "System_Char", ["short"] = "System_Int16", ["ushort"] = "System_UInt16",
        ["int"] = "System_Int32", ["uint"] = "System_UInt32", ["long"] = "System_Int64",
        ["ulong"] = "System_UInt64", ["float"] = "System_Single", ["double"] = "System_Double",
        ["decimal"] = "System_Decimal", ["string"] = "System_String", ["object"] = "System_Object"
    };

    public static string CanonicalTypeName(string arg)
    {
        return primitiveAliases.GetValueOrDefault(arg, arg);
    }

    public static HashSet<string> unresolvedTypes = new();

    public static  Node? StructBodyByName(string name)
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

    public static bool HasKlass(Node body)
    {
        return body.NamedChildren.Any(c => FindIdentifier(c)?.Text == "klass");
    }

    public static string FlagUnresolvedType(string type)
    {
        if (unresolvedTypes.Add(type)) Console.WriteLine($"could not resolve {type}");
        return type;
    }

    public static bool TrySubstitutedType(string type, out string val)
    {
        return TrySubstitutedType(type, out val, out _);
    }

    public static bool TrySubstitutedType(string type, out string val, out string e)
    {
        var name = type.StartsWith("struct ") ? type["struct ".Length..] : type;
        var pointer = false;
        if (name.EndsWith("*"))
        {
            name = name[..^1];
            pointer = true; 
        }
        if (name.StartsWith(dictPrefix) && name.EndsWith("__o"))
        {
            var args = name[dictPrefix.Length..^"__o".Length];
            if (nestedDictTypes.Any(n => args.StartsWith(n + "_") && !args.StartsWith(n + "__")))
            {
                val = type;
                e = string.Empty;
                return false;
            }

            var parts = args.Split("__");
            if (parts.Length != 2)
            {
                val = FlagUnresolvedType(type);
                e = string.Empty;
                return false;
            }

            string key = CanonicalTypeName(parts[0]), value = CanonicalTypeName(parts[1]);
            if (StructBodyByName(key + "_o") is null || StructBodyByName(value + "_o") is null)
            {
                val = FlagUnresolvedType(type);
                e = string.Empty;
                return false;
            }

            e = $"{key.Split("_")[^1]}, {value.Split("_")[^1]}";
            val = $"Dictionary<{e}>{(pointer ? "*" : "")}";
            return true;
        }

        if (name.StartsWith(listPrefix) && name.EndsWith("__o"))
        {
            var elem = CanonicalTypeName(name[listPrefix.Length..^"__o".Length]);
            if (StructBodyByName(elem + "_o") is not { } body)
            {
                val = FlagUnresolvedType(type);
                e = string.Empty;
                return false;
            }
            e = elem.Split("_")[^1];
            val = $"{(HasKlass(body) ? "Reference" : "Value")}List<{e}>{(pointer ? "*" : "")}";
            return true;
        }

        if (name.EndsWith("_array_array"))
        {
            var elem = CanonicalTypeName(name[..^"_array_array".Length]);
            if (StructBodyByName(elem + "_o") is not { } body)
            {
                val = FlagUnresolvedType(type);
                e = string.Empty;
                return false;
            }
            e = elem.Split("_")[^1];
            val = $"ValueArray<{(HasKlass(body) ? "Reference" : "Value")}Array<{e}>>{(pointer ? "*" : "")}";
            return true;
        }
        
        if (name.EndsWith("_array"))
        {
            var elem = CanonicalTypeName(name[..^"_array".Length]);
            if (StructBodyByName(elem + "_o") is not { } body)
            {
                val = FlagUnresolvedType(type);
                e = string.Empty;
                return false;
            }
            e = elem.Split("_")[^1];
            val = $"{(HasKlass(body) ? "Reference" : "Value")}Array<{e}>{(pointer ? "*" : "")}";
            return true;
        }

        val = type;
        e = string.Empty;
        return false;
    }

    public static void AppendFields(StringBuilder target, Node? body, bool skipVoid)
    {
        foreach (var str in GetFields(body, skipVoid))
        {
            target.Append(str);
        }
    }

    public static IEnumerable<string> GetFields(Node? body, bool skipVoid)
    {
        if (body is null) yield break;
        foreach (var field in body.NamedChildren.Where(c => c.Type == "field_declaration"))
        {
            var id = FindIdentifier(field);
            var declaredType = field.NamedChildren.FirstOrDefault(c =>
                c.Type is "type_identifier" or "primitive_type" or "struct_specifier" or "template_type");
            if (id is null || declaredType is null) continue;

            TrySubstitutedType(declaredType.Text, out var type);
            if (skipVoid && type == "void") continue;

            var isPointer = field.Text.Replace(declaredType.Text, "").Replace(id.Text, "").Contains('*');
            yield return $"\t{type.Replace("struct", "").Replace("_o", "")}{(isPointer ? "*" : "")} {id.Text};\n";
        }
    }

    public static Regex rex = new("^(?!\\d+$).+");

    public static string MergedStruct(string a)
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
            extra = $" : {head[(colon + 1)..].Trim().Replace("_Fields", "")}";
        if (!rex.IsMatch(a))
            return "";

        var hasStatics = structsByName.ContainsKey(a + "_StaticFields");
        var statics = new StringBuilder();
        if (hasStatics)
        {
            statics.Append($"struct {a}_c {{\n");
            statics.Append("\tchar pad[184];\n");
            statics.Append($"\tstruct {a}_StaticFields* static_fields;\n");
            statics.Append("};\n\n");
            statics.Append($"struct {a}_StaticFields {{\n");
            var body3 = GetBody(structsByName[a + "_StaticFields"]);
            if (body3 is null) return "";

            AppendFields(statics, body3, true);
            statics.Append("};\n\n");
        }

        var b = hasStatics ? a + "_c" : "void";
        builder.Append($"struct {a}{extra} {{\n");
        builder.Append($"\t{b}* klass;\n");
        builder.Append("\tvoid* monitor;\n");
        AppendFields(builder, fields, false);

        builder.Append("};\n\n");
        builder.Append(statics);
        return builder.ToString();
    }

    public static string[] strip =
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
    
    public static Dictionary<string, Node> structsByName2 = new();
    public static String secondContent;

    public static async Task Main(string[] args)
    {
        if (Directory.Exists(Path.GetDirectoryName(outputPath)))
            Directory.Delete(Path.GetDirectoryName(outputPath)!, true);
        using var language = new Language("C++");
        using var parser = new Parser(language);
        using var tree = parser.Parse(await File.ReadAllTextAsync(inputPath))!;
        IndexTypes(tree.RootNode, structsByName);
        if (!File.Exists(inputPath))
        {
            await Console.Error.WriteLineAsync($"input header not found: {inputPath}");
            return;
        }
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
        secondContent = header.ToString();
        File.WriteAllText(outputPath, secondContent);
        using var tree2 = parser.Parse(secondContent)!;
        IndexTypes(tree2.RootNode, structsByName2);
        // all types we want to generate .h/.sym files for
        List<string> types =
        [
            "*",
        ];

        var scriptJson = Path.Combine(dataRoot, "res", "script.json");
        if (File.Exists(scriptJson)) await SymbolExport.Run(scriptJson, Path.GetDirectoryName(outputPath)!, types);
        Delete(Path.GetDirectoryName(outputPath)!);
    }

    private static void Delete(string path)
    {
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            Delete(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }
}