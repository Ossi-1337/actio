using YamlDotNet.RepresentationModel;

namespace Actio.Core.Workflows;

internal static class YamlMergeKeyResolver
{
    public static YamlMergeKeyResolutionResult Resolve(YamlMappingNode root)
    {
        var errors = new List<string>();
        var resolved = ResolveNode(errors, root, "workflow");
        return resolved is YamlMappingNode resolvedRoot
            ? new YamlMergeKeyResolutionResult(resolvedRoot, errors)
            : new YamlMergeKeyResolutionResult(root, errors);
    }

    private static YamlNode ResolveNode(List<string> errors, YamlNode node, string path)
    {
        return node switch
        {
            YamlMappingNode mapping => ResolveMapping(errors, mapping, path),
            YamlSequenceNode sequence => ResolveSequence(errors, sequence, path),
            _ when node.NodeType == YamlNodeType.Alias => AddUnresolvedAliasError(errors, node, path),
            _ => node
        };
    }

    private static YamlMappingNode ResolveMapping(List<string> errors, YamlMappingNode mapping, string path)
    {
        var resolved = new YamlMappingNode();

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (IsMergeKey(keyNode))
            {
                MergeInto(errors, resolved, valueNode, $"{path}.<<");
                continue;
            }

            var key = ResolveNode(errors, keyNode, $"{path}.<key>");
            var childPath = GetChildPath(path, keyNode);
            var value = ResolveNode(errors, valueNode, childPath);
            AddOrSet(resolved, key, value, overwrite: true);
        }

        return resolved;
    }

    private static YamlSequenceNode ResolveSequence(List<string> errors, YamlSequenceNode sequence, string path)
    {
        var resolved = new YamlSequenceNode();

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            resolved.Add(ResolveNode(errors, sequence.Children[index], $"{path}[{index}]"));
        }

        return resolved;
    }

    private static void MergeInto(
        List<string> errors,
        YamlMappingNode target,
        YamlNode mergeNode,
        string path)
    {
        var resolvedMerge = ResolveNode(errors, mergeNode, path);

        if (resolvedMerge is YamlMappingNode mergeMap)
        {
            MergeMapping(target, mergeMap);
            return;
        }

        if (resolvedMerge is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                if (sequence.Children[index] is YamlMappingNode sequenceMap)
                {
                    MergeMapping(target, sequenceMap);
                    continue;
                }

                errors.Add($"{path}[{index}] must be a mapping.");
            }

            return;
        }

        errors.Add($"{path} must be a mapping or a list of mappings.");
    }

    private static void MergeMapping(YamlMappingNode target, YamlMappingNode source)
    {
        foreach (var (key, value) in source.Children)
        {
            AddOrSet(target, key, value, overwrite: false);
        }
    }

    private static YamlNode AddUnresolvedAliasError(List<string> errors, YamlNode node, string path)
    {
        errors.Add($"{path} contains an unresolved YAML alias.");
        return node;
    }

    private static string GetChildPath(string parentPath, YamlNode keyNode)
    {
        return keyNode is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? $"{parentPath}.{scalar.Value}"
            : $"{parentPath}.<key>";
    }

    private static bool IsMergeKey(YamlNode keyNode)
    {
        return keyNode is YamlScalarNode scalar &&
            string.Equals(scalar.Value, "<<", StringComparison.Ordinal);
    }

    private static void AddOrSet(
        YamlMappingNode mapping,
        YamlNode key,
        YamlNode value,
        bool overwrite)
    {
        var existingKey = FindEquivalentKey(mapping, key);
        if (existingKey is null)
        {
            mapping.Add(key, value);
            return;
        }

        if (overwrite)
        {
            mapping.Children[existingKey] = value;
        }
    }

    private static YamlNode? FindEquivalentKey(YamlMappingNode mapping, YamlNode key)
    {
        foreach (var existingKey in mapping.Children.Keys)
        {
            if (AreEquivalentKeys(existingKey, key))
            {
                return existingKey;
            }
        }

        return null;
    }

    private static bool AreEquivalentKeys(YamlNode left, YamlNode right)
    {
        return left is YamlScalarNode leftScalar &&
            right is YamlScalarNode rightScalar &&
            string.Equals(leftScalar.Value, rightScalar.Value, StringComparison.Ordinal);
    }
}

internal sealed record YamlMergeKeyResolutionResult(
    YamlMappingNode Root,
    IReadOnlyList<string> Errors);
