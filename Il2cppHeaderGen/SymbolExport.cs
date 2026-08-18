using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TreeSitter;

namespace Il2cppHeaderGen;

internal record ScriptMethod(long Address, string Name, string Signature);

internal class ScriptData
{
    [JsonPropertyName("ScriptMethod")] public List<ScriptMethod> Methods { get; init; } = [];
}

internal record Method(
    string Name,
    string ReturnType,
    List<(string Type, string Name)> Parameters,
    bool IsStatic,
    long Address);

internal static class SymbolExport
{
    private static readonly Dictionary<string, string> BuiltinTypes = new()
    {
        ["void"] = "v", ["bool"] = "b", ["char"] = "c",
        ["int8_t"] = "a", ["signed char"] = "a",
        ["uint8_t"] = "h", ["unsigned char"] = "h",
        ["int16_t"] = "s", ["short"] = "s",
        ["uint16_t"] = "t", ["unsigned short"] = "t",
        ["int32_t"] = "i", ["int"] = "i",
        ["uint32_t"] = "j", ["unsigned int"] = "j",
        ["int64_t"] = "l", ["long"] = "l", ["intptr_t"] = "l",
        ["uint64_t"] = "m", ["unsigned long"] = "m",
        ["uintptr_t"] = "m", ["il2cpp_array_size_t"] = "m",
        ["long long"] = "x", ["unsigned long long"] = "y",
        ["float"] = "f", ["double"] = "d"
    };

    public static async Task Run(string scriptJson, string outDir, List<string> types)
    {
        if (types.Count == 0) return;

        await using var stream = File.OpenRead(scriptJson);
        var data = await JsonSerializer.DeserializeAsync<ScriptData>(stream);
        if (data is null) return;

        var byType = types.ToDictionary(t => t, _ => new List<Method>());
        foreach (var scriptMethod in data.Methods)
        {
            var separator = scriptMethod.Name.IndexOf("$$", StringComparison.Ordinal);
            if (separator < 0 || !byType.TryGetValue(scriptMethod.Name[..separator], out var methods)) continue;
            if (ParsedMethod(scriptMethod, scriptMethod.Name[(separator + 2)..]) is { } parsed)
                methods.Add(parsed);
        }

        var a = new Dictionary<string, HashSet<string>>();
        Directory.CreateDirectory(outDir);
        foreach (var (type, methods) in byType)
        {
            if (methods.Count == 0)
            {
                Console.WriteLine($"No methods found for {type}");
                continue;
            }
            
            var dir = string.Join('/', type.Split(".").SkipLast(1));
            a.TryAdd(dir, new HashSet<string>());
            a[dir].Add(type);
            Directory.CreateDirectory(Path.Combine(outDir, dir));
            await File.WriteAllTextAsync(Path.Combine(outDir, dir, $"{type.Split(".")[^1]}.h"), HeaderFileContent(type, methods, outDir,dir));
            await File.WriteAllTextAsync(Path.Combine(outDir, dir, $"{type.Split(".")[^1]}.sym"), SymFileContent(type, methods));
        }

        foreach (var key in a.Keys)
        {
            StringBuilder builder = new();
            builder.Append("#pragma once\n\n");
            foreach (var type in a[key])
            {
                builder.Append($"#include \"{key}/{type.Split(".")[^1]}.h\"\n");
            }

            await File.WriteAllTextAsync(Path.Combine(outDir, $"{key}.h"), builder.ToString());
        }
    }

    private static Method? ParsedMethod(ScriptMethod scriptMethod, string name)
    {
        if (!IsValidIdentifier(name))
        {
            Console.WriteLine($"Skipping {scriptMethod.Name}: not a valid C++ identifier");
            return null;
        }

        int open = scriptMethod.Signature.IndexOf('('), close = scriptMethod.Signature.LastIndexOf(')');
        if (open < 0 || close < open) return null;

        var head = scriptMethod.Signature[..open].TrimEnd();
        var returnTypeEnd = head.LastIndexOf(' ');
        if (returnTypeEnd < 0) return null;

        var parameters = new List<(string, string)>();
        var isStatic = true;
        var parts = scriptMethod.Signature[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            var nameStart = part.LastIndexOf(' ');
            if (nameStart < 0) return null;
            string type = NormalizedType(part[..nameStart]), parameter = part[(nameStart + 1)..];

            if (i == 0 && parameter == "__this")
            {
                isStatic = false;
                continue;
            }

            if (i == parts.Length - 1 && type.Contains("MethodInfo")) continue;

            parameters.Add((type, IsValidIdentifier(parameter) ? parameter : $"a{parameters.Count + 1}"));
        }

        return new Method(name, NormalizedType(head[..returnTypeEnd]), parameters, isStatic, scriptMethod.Address);
    }
    
    private static bool Append(string input, HashSet<string> extras)
    {
        if (input.Contains("ReferenceArray"))
        {
            extras.Add("template <typename T> struct ReferenceArray");
            return true;
        }
        if (input.Contains("ValueArray"))
        {
            extras.Add("template <typename T> struct ValueArray");
            return true;
        }
        if (input.Contains("ValueList"))
        {
            extras.Add("template <typename T> struct ValueList");
            return true;
        }
        if (input.Contains("ReferenceList"))
        {
            extras.Add("template <typename T> struct ReferenceList");
            return true;
        }
        if (input.Contains("Dictionary"))
        {
            extras.Add("template <typename T> struct Dictionary");
        }

        return false;
    }

    private static void AddFields(IEnumerable<string> fields, HashSet<string> extras, string outDir, string d, StringBuilder builder)
    {
        foreach (var field in fields)
        {
            var t = field.Trim().Split(" ")[0];
            var c = BuiltinTypes.Any(a => t.Contains(a.Key));
            if (c)
                continue;
            var p = false;
            if (t.EndsWith("*"))
            {
                p = true;
                t = t[..^1];
            }

            if (t.EndsWith("_c"))
                continue;
            t = t.Split("_")[^1];
            if (p)
            {
                if (!Append(t, extras))
                    extras.Add(t);
                else
                {
                    var y = t.Split("<")[1].Split(">")[0];
                    foreach (var n in y.Split(", "))
                    {
                        extras.Add(n);
                    }
                }
            }
            else
            {
                string n = field.Trim().Split(" ")[0];
                var dir = string.Join("/", n.Split("_").SkipLast(1));
                n = n.Split("_")[^1];
                var relative = Path.GetRelativePath(Path.Combine(outDir, d), Path.Combine(outDir, dir));
                relative = relative.Replace("\\", "/");
                builder.Append($"#include \"{relative}/{n}.h\"\n");
            }
        }
    }

    private static void AppendFields(IEnumerable<string> enumerable, StringBuilder builder)
    {
        foreach (var field in enumerable)
        {
            var t = field.Trim().Split(" ")[0];
            var c = BuiltinTypes.Any(a => t.Contains(a.Key));
            var p = false;
            if (t.EndsWith("*"))
            {
                p = true;
                t = t[..^1];
            }

            t = t.EndsWith("_c") || c || string.IsNullOrEmpty(t.Split("_")[^1]) ? t : t.Split("_")[^1];
            builder.Append($"\t{t}{(p ? "*" : "")} {field.Trim().Split(" ")[1]}\n");
        }
    }

    private static string HeaderFileContent(string type, List<Method> methods, string outDir, string d)
    {
        var referencedTypes = methods
            .SelectMany(m => m.Parameters.Select(p => p.Type).Append(m.ReturnType))
            .Where(t => t != type)
            .Distinct()
            .Order(StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.Append("#pragma once\n\n");
        var extras = new HashSet<string>();
        foreach (var referenced in referencedTypes)
        {
            if (!Program.TrySubstitutedType(referenced, out var c, out var y))
            {
                c = BareTypeName(c);
                if (c is null) continue;
                c = c.Split("_")[^1];
                extras.Add(c);
            }
            else
            {
                foreach (var n in y.Split(", "))
                {
                    extras.Add(n);
                }
            }
            Append(c, extras);
        }

        var fields = Program.GetFields(Program.GetBody(Program.structsByName2[type.Replace(".", "_")]), false);
        var enumerable = fields as string[] ?? fields.ToArray();
        AddFields(enumerable, extras, outDir, d, builder);
        var s = type.Replace(".", "_") + "_StaticFields";
        if (Program.structsByName2.ContainsKey(s))
        {
            AddFields(Program.GetFields(Program.GetBody(Program.structsByName2[s]), false), extras, outDir, d, builder);
        }
        extras.Remove(type);
        extras.Remove("void");
        foreach (var extra in extras)
        {
            if (!string.IsNullOrEmpty(extra))
                builder.Append($"{(extra.Contains("struct") ? extra : "struct " + extra)};\n");
        }
        var name = type.Split(".")[^1];
        
        
        var b = Program.structsByName2[type.Replace(".", "_")];
        Node? bn = null;
        foreach (var child in b.NamedChildren)
        {
            if (child.Type == "base_class_clause")
            {
                bn = child.NamedChildren[^1];
                break;
            }
        }
        var baseName = bn != null ?  Program.secondContent.Substring(bn.StartIndex,bn.EndIndex - bn.StartIndex) : "";
        builder.Append("\n");
        if (Program.structsByName2.ContainsKey(s))
        {
            builder.Append($"struct {name}_c;\n");
            builder.Append($"struct {name}_StaticFields;\n");
        }
        builder.Append($"struct {name}_Fields;\n");
        builder.Append($"struct {name}_Methods;\n\n");
        if (!string.IsNullOrEmpty(baseName))
        {
            var dir = string.Join("/", baseName.Split("_").SkipLast(1));
            baseName = baseName.Split("_")[^1];
            var relative = Path.GetRelativePath(Path.Combine(outDir, d), Path.Combine(outDir, dir));
            relative = relative.Replace("\\", "/");
            builder.Append($"#include \"{relative}/{baseName}.h\"\n");
        }

        builder.Append($"\nstruct {name} {{\n");
        builder.Append($"\t{(Program.structsByName2.ContainsKey(s) ? name + "_c" : "void")}* klass;\n");
        builder.Append("\tvoid* monitor;\n");
        builder.Append($"\t{name}_Fields fields;\n");
        builder.Append($"\t{name}_Methods* methods() {{ return reinterpret_cast<{name}_Methods*>(this); }}\n");
        builder.Append("};\n");
        
        builder.Append($"\nstruct {name}_Fields {(!string.IsNullOrEmpty(baseName) ? $": {baseName}_Fields " : "")}{{\n");
        AppendFields(enumerable.Skip(2), builder);
        builder.Append("};\n");

        if (Program.structsByName2.ContainsKey(s))
        {
            builder.Append($"\nstruct {name}_c {{ \n");
            builder.Append("\tprivate: char pad[184];\n");
            builder.Append($"\tpublic: {name}_StaticFields* static_fields;\n");
            builder.Append("};\n");

            builder.Append($"\nstruct {name}_StaticFields {{\n");
            AppendFields(Program.GetFields(Program.GetBody(Program.structsByName2[s]), false), builder);
            builder.Append("};\n");
        }

        builder.Append($"\nstruct {name}_Methods {(!string.IsNullOrEmpty(baseName) ? $": {baseName}_Methods " : "")}{{\n");
        foreach (var method in methods)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p =>
            {
                var n = Program.TrySubstitutedType(p.Type, out var h);
                var c = BuiltinTypes.Any(a => p.Type.Contains(a.Key));
                h = h.Replace("_o", "");
                h = c || n || string.IsNullOrEmpty(h.Split("_")[^1]) ? h : h.Split("_")[^1];
                return $"{h} {p.Name}";
            }));
            var c = BuiltinTypes.Any(a => method.ReturnType.Contains(a.Key));
            var n = Program.TrySubstitutedType(method.ReturnType, out var v);
            v = v.Replace("_o", "");
            v = c || n || string.IsNullOrEmpty(v.Split("_")[^1]) ? v : v.Split("_")[^1];
            builder.Append($"\t{(method.IsStatic ? "static " : "")}{v} {method.Name}({parameters});\n");
        }
        builder.Append("};\n");
        
        return builder.ToString();
    }

    private static string SymFileContent(string type, List<Method> methods)
    {
        var builder = new StringBuilder("@game:100\n\n");
        var emitted = new HashSet<string>();

        foreach (var method in methods)
        {
            var symbol = MangledName(type, method);
            if (emitted.Add(symbol))
                builder.Append($"{symbol} = 0x{method.Address:X}\n");
            else
                Console.WriteLine($"Skipping duplicate symbol {symbol} for {type}::{method.Name}");
        }

        return builder.ToString();
    }

    private static string MangledName(string type, Method method)
    {
        var substitutions = new List<string> { type };
        var builder = new StringBuilder($"_ZN{type.Length}{type}{method.Name.Length}{method.Name}E");
        if (method.Parameters.Count == 0) return builder.Append('v').ToString();

        foreach (var (parameterType, _) in method.Parameters)
        {
            Program.TrySubstitutedType(parameterType, out var c);
            builder.Append(MangledType(c.Replace("_o", ""), substitutions));
        }

        return builder.ToString();
    }

    private static string MangledType(string type, List<string> substitutions)
    {
        if (BuiltinTypes.TryGetValue(type, out var code)) return code;

        var prior = substitutions.IndexOf(type);
        if (prior >= 0) return SubstitutionRef(prior);

        var encoded = type.EndsWith('*') ? "P" + MangledType(type[..^1].TrimEnd(), substitutions)
            : type.StartsWith("const ") ? "K" + MangledType(type[6..].TrimStart(), substitutions)
            : $"{type.Length}{type}";

        substitutions.Add(type);
        return encoded;
    }

    private static string SubstitutionRef(int index)
    {
        if (index == 0) return "S_";

        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var builder = new StringBuilder();
        for (var n = index - 1;; n /= 36)
        {
            builder.Insert(0, digits[n % 36]);
            if (n < 36) break;
        }

        return $"S{builder}_";
    }

    private static string NormalizedType(string type)
    {
        type = type.Trim();
        if (type.StartsWith("struct ")) type = type[7..];
        return type.Replace(" *", "*").Trim();
    }

    private static string? BareTypeName(string type)
    {
        var bare = type.TrimEnd('*').Trim();
        if (bare.StartsWith("const ")) bare = bare[6..].TrimStart();
        bare = bare.Replace("_o", "");
        return BuiltinTypes.ContainsKey(bare) || !IsValidIdentifier(bare) ? null : bare;
    }

    private static bool IsValidIdentifier(string s)
    {
        return s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}