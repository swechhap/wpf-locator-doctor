using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;

namespace WpfLocatorDoctor.Core.Source;

/// <summary>
/// Reads the project's C# files (syntax only, no compiling) to find custom WPF controls
/// and decide whether UI Automation can see them.
/// </summary>
public static class CodeBehindAnalyzer
{
    private sealed class ClassFacts
    {
        public required string Name { get; init; }
        public string? BaseName { get; set; }
        public bool OverridesPeer { get; set; }
        public SourceLocation? Location { get; set; }
    }

    /// <summary>Analyses C# files and returns custom controls keyed by class name.</summary>
    public static Dictionary<string, CustomControlInfo> Analyze(IEnumerable<(string Path, string DisplayPath)> csFiles)
    {
        var classes = new Dictionary<string, ClassFacts>(StringComparer.Ordinal);

        foreach (var (path, displayPath) in csFiles)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            CollectClasses(text, displayPath, classes);
        }

        return Build(classes);
    }

    /// <summary>Convenience overload for a single source text (used by tests).</summary>
    public static Dictionary<string, CustomControlInfo> AnalyzeText(string csharp, string displayPath = "code.cs")
    {
        var classes = new Dictionary<string, ClassFacts>(StringComparer.Ordinal);
        CollectClasses(csharp, displayPath, classes);
        return Build(classes);
    }

    private static Dictionary<string, CustomControlInfo> Build(Dictionary<string, ClassFacts> classes)
    {
        var result = new Dictionary<string, CustomControlInfo>(StringComparer.Ordinal);
        foreach (var c in classes.Values)
        {
            var (status, wpfBase) = Resolve(c.Name, classes, new HashSet<string>(StringComparer.Ordinal));
            if (wpfBase is null) continue; // not a WPF UI class (view model, service, ...)
            result[c.Name] = new CustomControlInfo(c.Name, c.BaseName, wpfBase, c.OverridesPeer, status, c.Location);
        }
        return result;
    }

    private static void CollectClasses(string text, string displayPath, Dictionary<string, ClassFacts> classes)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var name = cls.Identifier.ValueText;
            if (!classes.TryGetValue(name, out var facts))
            {
                facts = new ClassFacts { Name = name };
                classes[name] = facts;
            }

            var baseName = FirstBaseClassName(cls);
            if (baseName is not null && facts.BaseName is null)
            {
                facts.BaseName = baseName;
                var pos = cls.Identifier.GetLocation().GetLineSpan().StartLinePosition;
                facts.Location = new SourceLocation(displayPath, pos.Line + 1, pos.Character + 1);
            }
            facts.Location ??= LocationOf(cls, displayPath);

            if (cls.Members.OfType<MethodDeclarationSyntax>().Any(m =>
                    m.Identifier.ValueText == "OnCreateAutomationPeer" &&
                    m.Modifiers.Any(SyntaxKind.OverrideKeyword)))
            {
                facts.OverridesPeer = true;
            }
        }
    }

    private static SourceLocation LocationOf(ClassDeclarationSyntax cls, string displayPath)
    {
        var pos = cls.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        return new SourceLocation(displayPath, pos.Line + 1, pos.Character + 1);
    }

    /// <summary>The first base type that doesn't look like an interface (C# syntax alone can't tell them apart).</summary>
    private static string? FirstBaseClassName(ClassDeclarationSyntax cls)
    {
        if (cls.BaseList is null) return null;
        foreach (var t in cls.BaseList.Types)
        {
            var simple = SimpleName(t.Type);
            if (simple is null) continue;
            var looksLikeInterface = simple.Length > 1 && simple[0] == 'I' && char.IsUpper(simple[1]);
            if (!looksLikeInterface) return simple;
        }
        return null;
    }

    private static string? SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Walks up the inheritance chain until it reaches a WPF class we know about.</summary>
    private static (PeerStatus Status, string? WpfBase) Resolve(
        string className, Dictionary<string, ClassFacts> classes, HashSet<string> visiting)
    {
        if (!classes.TryGetValue(className, out var facts) || !visiting.Add(className))
            return (PeerStatus.Unknown, null);

        var baseName = facts.BaseName;
        if (baseName is null) return (PeerStatus.Unknown, null);

        PeerStatus status;
        string? wpfBase;

        if (classes.ContainsKey(baseName) && !ControlCatalog.IsKnownWpfType(baseName))
        {
            (status, wpfBase) = Resolve(baseName, classes, visiting);
        }
        else if (ControlCatalog.PeerBases.Contains(baseName))
        {
            (status, wpfBase) = (PeerStatus.Visible, baseName);
        }
        else if (ControlCatalog.NoPeerControlBases.Contains(baseName))
        {
            (status, wpfBase) = (PeerStatus.Invisible, baseName);
        }
        else if (ControlCatalog.NoPeerLayoutBases.Contains(baseName))
        {
            (status, wpfBase) = (PeerStatus.NoPeerLayout, baseName);
        }
        else
        {
            return (PeerStatus.Unknown, null);
        }

        if (facts.OverridesPeer) status = PeerStatus.Visible;
        return (status, wpfBase);
    }
}
