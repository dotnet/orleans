using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using org.apache.zookeeper;
using org.apache.zookeeper.data;
using Orleans.Runtime.Membership;

namespace UnitTests.MembershipTests;

internal sealed class ZooKeeperNativeFake
{
    internal sealed record Node(byte[] Data, int Version = 0, int ChildrenVersion = 0, int Flags = 0);
    internal abstract record Request(string Path);
    internal sealed record CreateRequest(string Path, byte[] Data, int Flags, List<ACL> Acl) : Request(Path);
    internal sealed record SetDataRequest(string Path, byte[]? Data, int Version) : Request(Path);
    internal sealed record DeleteRequest(string Path, int Version) : Request(Path);

    internal Dictionary<string, Node> Nodes { get; private set; } = new(StringComparer.Ordinal)
    {
        ["/"] = new([])
    };

    internal List<string> Calls { get; } = [];
    internal List<List<Request>> Transactions { get; } = [];
    internal List<SetDataRequest> Writes { get; } = [];
    internal Func<string, Task>? BeforeRead { get; set; }
    internal Func<string, Task>? AfterRead { get; set; }
    internal Func<List<Request>, Task>? BeforeMulti { get; set; }
    internal Func<string, Task>? BeforeWrite { get; set; }
    internal Func<Task>? OnSync { get; set; }

    internal ZooKeeperBasedMembershipTable.NativeOperations Operations => new(GetData, GetChildren, Sync, Multi, SetData);

    internal async Task<DataResult> GetData(string path)
    {
        Calls.Add("read " + path);
        if (BeforeRead is { } before)
        {
            await before(path);
        }

        var node = GetNode(Nodes, path);
        var result = CreateResult<DataResult>(node.Data, CreateStat(node));
        if (AfterRead is { } after)
        {
            await after(path);
        }

        return result;
    }

    private Task<ChildrenResult> GetChildren(string path)
    {
        Calls.Add("children " + path);
        var node = GetNode(Nodes, path);
        var children = Nodes.Keys.Where(key => key != path && Parent(key) == path)
            .Select(key => key[(key.LastIndexOf('/') + 1)..]).ToList();
        return Task.FromResult(CreateResult<ChildrenResult>(children, CreateStat(node)));
    }

    private async Task Sync(string path)
    {
        Calls.Add("sync " + path);
        if (OnSync is { } sync)
        {
            await sync();
        }
    }

    internal async Task Multi(List<Op> operations)
    {
        Calls.Add("multi");
        var requests = operations.Select(ToRequest).ToList();
        Transactions.Add(requests);
        if (BeforeMulti is { } before)
        {
            await before(requests);
        }

        // Apply against a copy so failed native preconditions roll back every earlier operation.
        var next = new Dictionary<string, Node>(Nodes, StringComparer.Ordinal);
        foreach (var request in requests)
        {
            switch (request)
            {
                case CreateRequest create:
                    var parent = Parent(create.Path);
                    var parentNode = GetNode(next, parent);
                    if (next.ContainsKey(create.Path))
                    {
                        throw new KeeperException.NodeExistsException(create.Path);
                    }

                    next.Add(create.Path, new(create.Data, Flags: create.Flags));
                    next[parent] = parentNode with { ChildrenVersion = parentNode.ChildrenVersion + 1 };
                    break;
                case SetDataRequest set:
                    Set(next, set.Path, set.Data, set.Version);
                    break;
                case DeleteRequest delete:
                    CheckVersion(next, delete.Path, delete.Version);
                    if (next.Keys.Any(key => key != delete.Path && Parent(key) == delete.Path))
                    {
                        throw new KeeperException.NotEmptyException(delete.Path);
                    }

                    next.Remove(delete.Path);
                    var parentPath = Parent(delete.Path);
                    var owner = GetNode(next, parentPath);
                    next[parentPath] = owner with { ChildrenVersion = owner.ChildrenVersion + 1 };
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected native request: {request.GetType().Name}");
            }
        }

        Nodes = next;
    }

    internal async Task<Stat> SetData(string path, byte[] data, int version)
    {
        Calls.Add("write " + path);
        Writes.Add(new(path, data, version));
        if (BeforeWrite is { } before)
        {
            await before(path);
        }

        Set(Nodes, path, data, version);
        return CreateStat(Nodes[path]);
    }

    internal static string RowPath(Orleans.Runtime.SiloAddress address) => "/" + address.ToParsableString();
    internal static string HeartbeatPath(Orleans.Runtime.SiloAddress address) => RowPath(address) + "/IAmAlive";

    private static Request ToRequest(Op operation)
    {
        // ZooKeeperNetEx exposes only an Op's type and path publicly; decoding its payload requires this SDK seam.
        var result = GetValue<object>(operation, "toRequestRecord", visibility: BindingFlags.NonPublic);
        var path = GetValue<string>(result, "getPath");
        return result.GetType().Name switch
        {
            "CreateRequest" => new CreateRequest(path, GetValue<byte[]>(result, "getData"),
                GetValue<int>(result, "getFlags"), GetValue<List<ACL>>(result, "getAcl")),
            "SetDataRequest" => new SetDataRequest(path, GetValue<byte[]?>(result, "getData", allowNull: true), GetValue<int>(result, "getVersion")),
            "DeleteRequest" => new DeleteRequest(path, GetValue<int>(result, "getVersion")),
            _ => throw ApiMismatch(result.GetType(), "toRequestRecord", $"unsupported request type {result.GetType().FullName}")
        };
    }

    internal static T GetValue<T>(object request, string methodName, bool allowNull = false, BindingFlags visibility = BindingFlags.Public)
    {
        var type = request.GetType();
        var method = type.GetMethod(methodName, BindingFlags.Instance | visibility, Type.EmptyTypes)
            ?? throw ApiMismatch(type, methodName, "expected a parameterless instance method");
        var result = method.Invoke(request, null);
        if (result is T value)
        {
            return value;
        }

        if (result is null && allowNull && default(T) is null)
        {
            return default!;
        }

        throw ApiMismatch(type, methodName, $"expected {typeof(T).FullName}, received {result?.GetType().FullName ?? "null"}");
    }

    private static InvalidOperationException ApiMismatch(Type type, string methodName, string detail) =>
        new($"ZooKeeperNativeFake cannot decode {type.FullName}.{methodName}: {detail}. "
            + $"Update the fake for the installed ZooKeeperNetEx API ({typeof(Op).Assembly.FullName}).");

    internal static T CreateResult<T>(params object[] arguments)
    {
        var parameterTypes = arguments.Select(argument => argument.GetType()).ToArray();
        var constructor = typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: parameterTypes, modifiers: null)
            ?? throw ApiMismatch(typeof(T), ".ctor",
                $"expected a non-public instance constructor accepting ({string.Join(", ", parameterTypes.Select(type => type.FullName))})");
        return (T)constructor.Invoke(arguments);
    }

    private static Stat CreateStat(Node node) => new(0, 0, 0, 0, node.Version, node.ChildrenVersion, 0, 0, 0, 0, 0);
    private static string Parent(string path) => path.LastIndexOf('/') is var index && index > 0 ? path[..index] : "/";
    private static Node GetNode(Dictionary<string, Node> nodes, string path) =>
        nodes.TryGetValue(path, out var node) ? node : throw new KeeperException.NoNodeException(path);

    private static Node CheckVersion(Dictionary<string, Node> nodes, string path, int version)
    {
        var node = GetNode(nodes, path);
        if (version != -1 && node.Version != version)
        {
            throw new KeeperException.BadVersionException(path);
        }

        return node;
    }

    private static void Set(Dictionary<string, Node> nodes, string path, byte[]? data, int version)
    {
        var node = CheckVersion(nodes, path, version);
        nodes[path] = node with { Data = data ?? [], Version = node.Version + 1 };
    }
}
