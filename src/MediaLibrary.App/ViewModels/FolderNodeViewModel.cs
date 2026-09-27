using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaLibrary.Core.Naming;
using MediaLibrary.Data.Repositories;

namespace MediaLibrary.App.ViewModels;

/// <summary>左侧文件夹树的一个节点。</summary>
public partial class FolderNodeViewModel : ObservableObject
{
    public FolderNodeViewModel(string folderKey, string name)
    {
        FolderKey = folderKey;
        Name = name;
    }

    /// <summary>库内相对目录（正斜杠）。空串代表库根。</summary>
    public string FolderKey { get; }

    public string Name { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    /// <summary>该目录自身（不含子目录）的素材数。</summary>
    public long OwnAssetCount { get; set; }

    /// <summary>含所有子目录的素材总数。</summary>
    [ObservableProperty]
    private long _totalAssetCount;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasChildren => Children.Count > 0;

    public string CountText => TotalAssetCount > 0 ? TotalAssetCount.ToString() : "";

    /// <summary>
    /// 由扁平的 FolderKey → 数量列表构建层级树。
    /// 中间层级即使自身没有素材也会被创建出来（否则树会断链）。
    /// </summary>
    public static ObservableCollection<FolderNodeViewModel> BuildTree(IEnumerable<FolderSummary> summaries)
    {
        var lookup = new Dictionary<string, FolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var roots = new ObservableCollection<FolderNodeViewModel>();

        foreach (var summary in summaries.OrderBy(s => s.FolderKey, StringComparer.OrdinalIgnoreCase))
        {
            // 库根本身的素材（FolderKey 为空）单独挂一个节点
            if (string.IsNullOrEmpty(summary.FolderKey))
            {
                var rootNode = new FolderNodeViewModel("", "库根目录");
                rootNode.OwnAssetCount = summary.AssetCount;
                lookup[""] = rootNode;
                roots.Add(rootNode);
                continue;
            }

            var parts = summary.FolderKey.Split(LibraryPathHelper.Separator,
                StringSplitOptions.RemoveEmptyEntries);

            FolderNodeViewModel? parent = null;
            var accumulated = "";

            foreach (var part in parts)
            {
                accumulated = accumulated.Length == 0 ? part : $"{accumulated}{LibraryPathHelper.Separator}{part}";

                if (!lookup.TryGetValue(accumulated, out var node))
                {
                    node = new FolderNodeViewModel(accumulated, part);
                    lookup[accumulated] = node;

                    if (parent is null) roots.Add(node);
                    else parent.Children.Add(node);
                }

                parent = node;
            }

            if (parent is not null) parent.OwnAssetCount = summary.AssetCount;
        }

        foreach (var root in roots) ComputeTotals(root);

        // 目录多的库默认收起二级以下，保持"简洁"（设计文档 §6.4）
        foreach (var root in roots) root.IsExpanded = roots.Count <= 12;

        return roots;
    }

    private static long ComputeTotals(FolderNodeViewModel node)
    {
        var total = node.OwnAssetCount;
        foreach (var child in node.Children) total += ComputeTotals(child);

        node.TotalAssetCount = total;
        return total;
    }

    /// <summary>在树里按 FolderKey 查找节点。</summary>
    public static FolderNodeViewModel? Find(
        IEnumerable<FolderNodeViewModel> nodes, string folderKey)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FolderKey, folderKey, StringComparison.OrdinalIgnoreCase))
                return node;

            var found = Find(node.Children, folderKey);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// 把磁盘上真实存在、但树里还没有的目录补进树（右键"新建文件夹"后立即可见，
    /// 即使文件夹还没有素材）。中间层级一并补齐。
    /// </summary>
    public static FolderNodeViewModel EnsureFolderNode(
        ObservableCollection<FolderNodeViewModel> roots, string folderKey)
    {
        var existing = Find(roots, folderKey);
        if (existing is not null) return existing;

        var parts = folderKey.Split(LibraryPathHelper.Separator,
            StringSplitOptions.RemoveEmptyEntries);

        FolderNodeViewModel? parent = null;
        var accumulated = "";

        foreach (var part in parts)
        {
            accumulated = accumulated.Length == 0
                ? part
                : $"{accumulated}{LibraryPathHelper.Separator}{part}";

            var node = Find(roots, accumulated);

            if (node is null)
            {
                node = new FolderNodeViewModel(accumulated, part);

                if (parent is null) roots.Add(node);
                else parent.Children.Add(node);
            }

            parent = node;
        }

        return parent!;
    }
}
