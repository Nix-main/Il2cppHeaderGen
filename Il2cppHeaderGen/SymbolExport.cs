using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2cppHeaderGen;

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

        Directory.CreateDirectory(outDir);
        foreach (var (type, methods) in byType)
        {
            if (methods.Count == 0)
            {
                Console.WriteLine($"No methods found for {type}");
                continue;
            }

            await File.WriteAllTextAsync(Path.Combine(outDir, $"{type}.h"), HeaderFileContent(type, methods));
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{type}.sym"), SymFileContent(type, methods));
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

    private static string HeaderFileContent(string type, List<Method> methods)
    {
        var referencedTypes = methods
            .SelectMany(m => m.Parameters.Select(p => p.Type).Append(m.ReturnType))
            .Select(BareTypeName)
            .OfType<string>()
            .Where(t => t != type)
            .Distinct()
            .Order(StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.Append("#pragma once\n\n");
        foreach (var referenced in referencedTypes)
        {
            if (!Program.TrySubstitutedType(referenced, out var c))
            {
                c = c.Split("_")[^1];
            }
            if (!string.IsNullOrEmpty(c))
                builder.Append($"struct {c};\n");
        }

        builder.Append($"\nstruct {type.Split(".")[^1]} {{\n");
        foreach (var method in methods)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p =>
            {
                bool n = Program.TrySubstitutedType(p.Type, out var h);
                bool c = Program.primitiveAliases.Any(a => p.Type.Contains(a.Key));
                h = h.Replace("_o", "");
                if (h.Contains("array"))
                {
                    Console.WriteLine(p.Type);
                    Console.WriteLine(h);
                }

                h = c || n ? h : h.Split("_")[^1];
                return $"{h} {p.Name}";
            }));
            bool c = Program.primitiveAliases.Any(a => method.ReturnType.Contains(a.Key));
            var n = Program.TrySubstitutedType(method.ReturnType, out var v);
            v = v.Replace("_o", "");
            v = c || n ? v : v.Split("_")[^1];
            builder.Append($"\t{(method.IsStatic ? "static " : "")}{v} {method.Name}({parameters});\n");
        }

        return builder.Append("};\n").ToString();
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