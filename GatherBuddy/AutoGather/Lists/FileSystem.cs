using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GatherBuddy.AutoGather.Lists;

public enum FileSystemChangeType
{
    ObjectRenamed,
    ObjectRemoved,
    FolderAdded,
    LeafAdded,
    ObjectMoved,
}

public interface ISortMode<T> where T : class
{
    IEnumerable<FileSystem<T>.IPath> GetChildren(FileSystem<T>.Folder folder);
}

public sealed class FileSystem<T> : IEnumerable<KeyValuePair<T, FileSystem<T>.Leaf>> where T : class
{
    public delegate void ChangeDelegate(FileSystemChangeType type, IPath changedObject, IPath? previousParent, IPath? newParent);

    public event ChangeDelegate? Changed;

    public interface IPath
    {
        string Name { get; }

        Folder Parent { get; }

        bool IsRoot { get; }

        string FullName();
    }

    public sealed class Folder : IPath
    {
        internal readonly List<IPath> Children = [];

        internal Folder(Folder? parent, string name)
        {
            Parent = parent ?? this;
            Name = name;
        }

        public string Name { get; internal set; }
        public Folder Parent { get; internal set; }

        public bool IsRoot
            => ReferenceEquals(Parent, this);

        public List<Folder> GetSubFolders()
        {
            var folders = new List<Folder>();
            foreach (var child in Children)
            {
                if (child is Folder folder)
                {
                    folders.Add(folder);
                }
            }

            folders.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return folders;
        }

        public IEnumerable<Leaf> GetLeaves()
        {
            foreach (var child in Children)
            {
                if (child is Leaf leaf)
                {
                    yield return leaf;
                }
            }
        }

        public IEnumerable<IPath> GetChildren(ISortMode<T> mode)
            => mode.GetChildren(this);

        public List<IPath> GetAllDescendants(ISortMode<T> mode)
        {
            var descendants = new List<IPath>();
            AddDescendants(this, mode, descendants);
            return descendants;
        }

        private static void AddDescendants(Folder folder, ISortMode<T> mode, List<IPath> descendants)
        {
            foreach (var child in folder.GetChildren(mode))
            {
                descendants.Add(child);
                if (child is Folder subFolder)
                {
                    AddDescendants(subFolder, mode, descendants);
                }
            }
        }

        public int CountLeaves()
        {
            var count = 0;
            foreach (var child in Children)
            {
                count += child is Folder folder ? folder.CountLeaves() : 1;
            }

            return count;
        }

        public bool Contains(IPath path)
        {
            for (var current = path; ; current = current.Parent)
            {
                if (ReferenceEquals(current, this))
                {
                    return true;
                }

                if (current.IsRoot || !current.Parent.Children.Contains(current))
                {
                    return false;
                }
            }
        }

        public string FullName()
            => IsRoot ? string.Empty : Parent.IsRoot ? Name : $"{Parent.FullName()}/{Name}";

        public override string ToString()
            => FullName();
    }

    public sealed class Leaf : IPath
    {
        internal Leaf(Folder parent, string name, T value)
        {
            Parent = parent;
            Name = name;
            Value = value;
        }

        public T Value { get; }
        public string Name { get; internal set; }
        public Folder Parent { get; internal set; }

        public bool IsRoot
            => false;

        public string FullName()
            => Parent.IsRoot ? Name : $"{Parent.FullName()}/{Name}";

        public override string ToString()
            => FullName();
    }

    private readonly Dictionary<T, Leaf> _leaves = [];

    public Folder Root { get; } = new(null, string.Empty);

    public Dictionary<T, Leaf>.KeyCollection Values
        => _leaves.Keys;

    public bool TryGetValue(T value, [NotNullWhen(true)] out Leaf? leaf)
        => _leaves.TryGetValue(value, out leaf);

    public Leaf CreateLeaf(Folder parent, string name, T value)
    {
        name = FileSystemNames.Fix(name);
        if (Find(parent, name) != null)
        {
            throw new InvalidOperationException($"Could not add {name} to {parent.FullName()}: something of that name already exists.");
        }

        var leaf = new Leaf(parent, name, value);
        parent.Children.Add(leaf);
        _leaves[value] = leaf;
        Changed?.Invoke(FileSystemChangeType.LeafAdded, leaf, null, parent);
        return leaf;
    }

    public Leaf CreateDuplicateLeaf(Folder parent, string name, T value)
    {
        name = FileSystemNames.Fix(name);
        while (Find(parent, name) != null)
        {
            name = FileSystemNames.IncrementDuplicate(name);
        }

        return CreateLeaf(parent, name, value);
    }

    public Folder CreateFolder(Folder parent, string name)
    {
        name = FileSystemNames.Fix(name);
        if (Find(parent, name) != null)
        {
            throw new InvalidOperationException($"Could not add folder {name} to {parent.FullName()}: something of that name already exists.");
        }

        var folder = new Folder(parent, name);
        parent.Children.Add(folder);
        Changed?.Invoke(FileSystemChangeType.FolderAdded, folder, null, parent);
        return folder;
    }

    public (Folder Folder, bool Created) FindOrCreateFolder(Folder parent, string name)
    {
        name = FileSystemNames.Fix(name);
        return Find(parent, name) switch
        {
            Folder folder => (folder, false),
            null => (CreateFolder(parent, name), true),
            _ => throw new InvalidOperationException($"Could not add folder {name} to {parent.FullName()}: a list of that name exists."),
        };
    }

    public void Delete(IPath path)
    {
        if (path.IsRoot)
        {
            throw new InvalidOperationException("Can not delete the root folder.");
        }

        var parent = path.Parent;
        parent.Children.Remove(path);
        switch (path)
        {
            case Leaf leaf:
                _leaves.Remove(leaf.Value);
                break;
            case Folder folder:
                RemoveLeaves(folder);
                break;
        }

        Changed?.Invoke(FileSystemChangeType.ObjectRemoved, path, parent, null);
    }

    public void Rename(IPath path, string newName)
    {
        if (path.IsRoot)
        {
            throw new InvalidOperationException("Can not rename the root folder.");
        }

        newName = FileSystemNames.Fix(newName);
        if (newName == path.Name)
        {
            return;
        }

        var existing = Find(path.Parent, newName);
        if (existing != null && !ReferenceEquals(existing, path))
        {
            throw new InvalidOperationException($"Could not rename {path.FullName()} to {newName}: something of that name already exists.");
        }

        switch (path)
        {
            case Leaf leaf:
                leaf.Name = newName;
                break;
            case Folder folder:
                folder.Name = newName;
                break;
        }

        Changed?.Invoke(FileSystemChangeType.ObjectRenamed, path, path.Parent, path.Parent);
    }

    public void Move(IPath path, Folder newParent)
    {
        if (path.IsRoot)
        {
            throw new InvalidOperationException("Can not move the root folder.");
        }

        var oldParent = path.Parent;
        if (ReferenceEquals(oldParent, newParent))
        {
            return;
        }

        if (path is Folder folder && folder.Contains(newParent))
        {
            throw new InvalidOperationException($"Can not move {path.FullName()} into itself.");
        }

        if (Find(newParent, path.Name) != null)
        {
            throw new InvalidOperationException($"Could not move {path.FullName()}: {newParent.FullName()} already holds something named {path.Name}.");
        }

        oldParent.Children.Remove(path);
        newParent.Children.Add(path);
        switch (path)
        {
            case Leaf leaf:
                leaf.Parent = newParent;
                break;
            case Folder sub:
                sub.Parent = newParent;
                break;
        }

        Changed?.Invoke(FileSystemChangeType.ObjectMoved, path, oldParent, newParent);
    }

    public IEnumerator<KeyValuePair<T, Leaf>> GetEnumerator()
        => _leaves.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    private static IPath? Find(Folder parent, string name)
    {
        foreach (var child in parent.Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    private void RemoveLeaves(Folder folder)
    {
        foreach (var child in folder.Children)
        {
            switch (child)
            {
                case Leaf leaf:
                    _leaves.Remove(leaf.Value);
                    break;
                case Folder subFolder:
                    RemoveLeaves(subFolder);
                    break;
            }
        }
    }
}

public static partial class FileSystemNames
{
    public static string Fix(string name)
    {
        var fixedName = name.Replace('/', '\\').Trim();
        return fixedName.Length == 0 ? "<None>" : fixedName;
    }

    public static string IncrementDuplicate(string name)
    {
        var match = DuplicateNameRegex().Match(name);
        return match.Success ? $"{match.Groups["BaseName"].Value} ({int.Parse(match.Groups["Amount"].Value) + 1})" : $"{name} (2)";
    }

    [GeneratedRegex(@"^(?<BaseName>.*) \((?<Amount>[2-9]|\d\d+)\)$", RegexOptions.ExplicitCapture)]
    private static partial Regex DuplicateNameRegex();
}
