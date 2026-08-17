using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TreeSitter;

using var language = new Language("C++");
using var parser = new Parser(language);
var source = File.ReadAllText(@"D:\Among Us Exefs Modding\il2cpp.h");
using var tree = parser.Parse(source)!;

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

Node? GetBody(Node node) => node.NamedChildren.FirstOrDefault(c => c.Type == "field_declaration_list");

Node? GetTypeNode(Node field) => field.NamedChildren.FirstOrDefault(c => c.Type is "type_identifier" or "primitive_type" or "struct_specifier");

Node? FindIdentifier(Node node)
{
    if (node.Type is "field_identifier") return node;
    return node.NamedChildren.Select(FindIdentifier).FirstOrDefault(n => n is not null);
}

string ReplaceArray(string type) 
{
    if (type.Contains("_array"))
    {
        try {
            string a = type.Split(" ")[1].Split("_array")[0];
            var body = GetBody(structsByName[a + "_o"]);
            if (body is null) return "";
            bool useRef = body.NamedChildren.Any(c => FindIdentifier(c)!.Text == "klass");
            return useRef ? $"ReferenceArray<{a}>" : $"ValueArray<{a}>";
        } catch (IndexOutOfRangeException) { }
    }
    return type;
}

string ReplaceList(string type) 
{
    if (type.Contains("System_Collections_Generic_List"))
    {
        try {
            string a = type.Split(" ")[1].Split("System_Collections_Generic_List_")[1].Replace("__", "_");
            var body = GetBody(structsByName[a]);
            if (body is null) return "";
            bool useRef = body.NamedChildren.Any(c => FindIdentifier(c)!.Text == "klass");
            return useRef ? $"ReferenceList<{a}>" : $"ValueList<{a}>";
        } catch (IndexOutOfRangeException) { }
    }
    return type;
}

string ReplaceDictionary(string type) 
{
    if (type.Contains("System_Collections_Generic_Dictionary"))
    {
        try
        {
            string a = type.Split(" ")[1].Split("System_Collections_Generic_Dictionary_")[1].Replace("__", "_");
            var body = GetBody(structsByName[a]);
            if (body is null) return "";
            return $"Dictionary<{a}>";
        } catch (IndexOutOfRangeException) { }
    }
    return type;
}

var rex = new Regex("^(?!\\d+$).+");    

string MergeStructs(string a)
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
    string extra = structsByName[a + "_Fields"].Text.Contains(":") ? structsByName[a + "_Fields"].Text.Split(":")[1].Split("\n")[0].Trim() : "";
    extra = extra.Replace("_Fields", "");
    extra = extra.Split("_")[extra.Split("_").Length - 1];
    if (!string.IsNullOrEmpty(extra))
        extra = $" : {extra}";
    string f = a.Split("_")[a.Split("_").Length - 1];
    if (!rex.IsMatch(f))
        return "";

    var hasStatics = structsByName.ContainsKey(f + "_StaticFields");
    StringBuilder statics = new StringBuilder();
    if (hasStatics)
    {
        statics.Append($"struct {f}_c {{\n");
        statics.Append("\tchar pad[184];\n");
        statics.Append($"\tstruct {f}_StaticFields* static_fields;\n");
        statics.Append("};\n\n");
        statics.Append($"struct {f}_StaticFields {{\n");
        var body3 = GetBody(structsByName[f + "_StaticFields"]);
        if (body3 is null) return "";

        foreach (var field in body3.NamedChildren.Where(c => c.Type == "field_declaration"))
        {
            var id = FindIdentifier(field);
            string typeNode = GetTypeNode(field)!.Text;
            try
            {
                typeNode = ReplaceDictionary(ReplaceList(ReplaceArray(GetTypeNode(field)!.Text)));
            }
            catch (KeyNotFoundException e)
            {
                Console.WriteLine($"Encountered error while merging {id!.Text} in {a}, likely due to type {typeNode}: {e.Message} at line {e.StackTrace!.Split('\n')[1].Split(':')[2]}");
            }

            if (typeNode == "void") continue;

            var isPointer = field.Text.Replace(typeNode, "").Replace(id!.Text, "").Contains('*');
            statics.Append($"\t{typeNode.Replace("_o", "")}{(isPointer ? "*" : "")} {id.Text};\n");
        }
        statics.Append("};\n\n");
    }

    string b = hasStatics ? f + "_c" : "void*";
    builder.Append($"struct {f}{extra}\n");
    builder.Append($"\t{b} klass;\n");
    builder.Append("\tvoid* monitor;\n");
    foreach (var field in fields.NamedChildren.Where(c => c.Type == "field_declaration"))
    {
        var id = FindIdentifier(field);
        string typeNode = GetTypeNode(field)!.Text;
        try
        {
            typeNode = ReplaceDictionary(ReplaceList(ReplaceArray(GetTypeNode(field)!.Text)));
        }
        catch (KeyNotFoundException e)
        {
            Console.WriteLine($"Encountered error while merging {id!.Text} in {a}, likely due to type {typeNode}: {e.Message} at line {e.StackTrace!.Split('\n')[1].Split(':')[2]}");
        }
        var isPointer = field.Text.Replace(typeNode, "").Replace(id!.Text, "").Contains('*');
        builder.Append($"\t{typeNode.Replace("_o", "")}{(isPointer ? "*" : "")} {id.Text};\n");
    }

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

StringBuilder header = new StringBuilder();
foreach (var key in structsByName.Keys)
{
    string v = new string(key.SkipLast(2).ToArray());
    if (!key.EndsWith("_o") || strip.Any(s => v.StartsWith(s) || v.EndsWith(s)))
        continue;
    header.Append(MergeStructs(v));
}

await using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Il2cppHeaderGen.add.h");
using var reader = new StreamReader(st!);
string content = await reader.ReadToEndAsync();
header.Append(content);

File.WriteAllText("il2cpp.h", header.ToString());