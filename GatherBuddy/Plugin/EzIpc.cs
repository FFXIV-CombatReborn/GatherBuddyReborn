using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace GatherBuddy.Plugin;

internal enum SafeWrapper
{
    None,
    IPCException
}

internal class EzIPCDisposalToken
{
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Field | AttributeTargets.Property)]
internal class EzIPCAttribute : Attribute
{
    public string? IPCName { get; }
    public bool ApplyPrefix { get; }

    public EzIPCAttribute(string? ipcName = null, bool applyPrefix = true)
    {
        IPCName = ipcName;
        ApplyPrefix = applyPrefix;
    }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
internal class EzIPCEventAttribute : Attribute
{
    public string? IPCName { get; }
    public bool ApplyPrefix { get; }

    public EzIPCEventAttribute(string? ipcName = null, bool applyPrefix = true)
    {
        IPCName = ipcName;
        ApplyPrefix = applyPrefix;
    }
}

internal static class EzIPC
{
    private static readonly List<Action> DisposalActions = new();

    public static EzIPCDisposalToken[] Init(object instance, string prefix, SafeWrapper safeWrapper = SafeWrapper.None)
    {
        var type = instance.GetType();
        InitType(instance, type, prefix, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return Array.Empty<EzIPCDisposalToken>();
    }

    public static EzIPCDisposalToken[] Init(Type staticType, string prefix, SafeWrapper safeWrapper = SafeWrapper.None)
    {
        InitType(null, staticType, prefix, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return Array.Empty<EzIPCDisposalToken>();
    }

    public static void Dispose()
    {
        foreach (var action in DisposalActions)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Error during IPC disposal: {e.Message}\n{e.StackTrace}");
            }
        }
        DisposalActions.Clear();
    }

    private static void InitType(object? instance, Type type, string prefix, BindingFlags flags)
    {
        InitProviders(instance, type, prefix, flags);
        InitSubscribers(instance, type, prefix, flags);
        InitEvents(instance, type, prefix, flags);
    }

    private static void InitProviders(object? instance, Type type, string prefix, BindingFlags flags)
    {
        foreach (var method in type.GetMethods(flags))
        {
            var attr = method.GetCustomAttribute<EzIPCAttribute>();
            if (attr == null) continue;

            var ipcName = attr.IPCName ?? method.Name;
            var fullName = attr.ApplyPrefix ? $"{prefix}.{ipcName}" : ipcName;

            try
            {
                var parameters = method.GetParameters();
                var returnType = method.ReturnType;
                var isAction = returnType == typeof(void);

                if (isAction)
                {
                    RegisterActionProvider(fullName, method, instance, parameters.Length);
                }
                else
                {
                    RegisterFuncProvider(fullName, method, instance, parameters, returnType);
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to register IPC provider {fullName}: {e.Message}");
            }
        }
    }

    private static void InitSubscribers(object? instance, Type type, string prefix, BindingFlags flags)
    {
        foreach (var field in type.GetFields(flags))
        {
            var attr = field.GetCustomAttribute<EzIPCAttribute>();
            if (attr == null) continue;

            var ipcName = attr.IPCName ?? field.Name;
            var fullName = attr.ApplyPrefix ? $"{prefix}.{ipcName}" : ipcName;

            try
            {
                var fieldType = field.FieldType;
                if (!fieldType.IsGenericType && fieldType != typeof(Action))
                    continue;

                var subscriber = CreateSubscriber(fullName, fieldType);
                if (subscriber != null)
                {
                    field.SetValue(instance, subscriber);
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to initialize IPC subscriber {fullName}: {e.Message}");
            }
        }
    }

    private static void InitEvents(object? instance, Type type, string prefix, BindingFlags flags)
    {
        foreach (var field in type.GetFields(flags))
        {
            var attr = field.GetCustomAttribute<EzIPCEventAttribute>();
            if (attr == null) continue;

            var ipcName = attr.IPCName ?? field.Name;
            var fullName = attr.ApplyPrefix ? $"{prefix}.{ipcName}" : ipcName;

            try
            {
                GatherBuddy.Log.Debug($"Initializing IPC event {fullName} (type: {field.FieldType.Name})");
                var fieldType = field.FieldType;
                var eventProvider = CreateEventProvider(fullName, fieldType);
                if (eventProvider != null)
                {
                    field.SetValue(instance, eventProvider);
                    GatherBuddy.Log.Debug($"Successfully initialized IPC event {fullName}");
                }
                else
                {
                    GatherBuddy.Log.Error($"Failed to create event provider for {fullName} - CreateEventProvider returned null");
                }
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"Failed to initialize IPC event {fullName}: {e.Message}\n{e.StackTrace}");
            }
        }
    }

    private static void RegisterActionProvider(string name, MethodInfo method, object? instance, int paramCount)
    {
        switch (paramCount)
        {
            case 0:
            {
                var provider = Dalamud.PluginInterface.GetIpcProvider<object>(name);
                var action = (Action)Delegate.CreateDelegate(typeof(Action), instance, method);
                provider.RegisterAction(action);
                DisposalActions.Add(() => provider?.UnregisterAction());
                break;
            }
            case 1:
            {
                var p1Type = method.GetParameters()[0].ParameterType;
                var providerType = typeof(ICallGateProvider<,>).MakeGenericType(p1Type, typeof(object));
                var getProviderMethod = typeof(IDalamudPluginInterface)
                    .GetMethods()
                    .First(m => m.Name == "GetIpcProvider" && m.GetGenericArguments().Length == 2)
                    .MakeGenericMethod(p1Type, typeof(object));
                var provider = getProviderMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
                
                var actionType = typeof(Action<>).MakeGenericType(p1Type);
                var action = Delegate.CreateDelegate(actionType, instance, method);
                
                providerType.GetMethod("RegisterAction")!.Invoke(provider, new object[] { action });
                DisposalActions.Add(() =>
                {
                    if (provider is ICallGateProvider baseProvider)
                        baseProvider.UnregisterAction();
                });
                break;
            }
        }
    }

    private static void RegisterFuncProvider(string name, MethodInfo method, object? instance, ParameterInfo[] parameters, Type returnType)
    {
        var paramTypes = parameters.Select(p => p.ParameterType).ToArray();
        var allTypes = paramTypes.Concat(new[] { returnType }).ToArray();
        
        Type providerType;
        Type delegateType;
        
        if (paramTypes.Length == 0)
        {
            providerType = typeof(ICallGateProvider<>).MakeGenericType(returnType);
            delegateType = typeof(Func<>).MakeGenericType(returnType);
        }
        else if (paramTypes.Length == 1)
        {
            providerType = typeof(ICallGateProvider<,>).MakeGenericType(allTypes);
            delegateType = typeof(Func<,>).MakeGenericType(allTypes);
        }
        else
        {
            return;
        }

        var getProviderMethod = typeof(IDalamudPluginInterface)
            .GetMethods()
            .First(m => m.Name == "GetIpcProvider" && m.GetGenericArguments().Length == allTypes.Length)
            .MakeGenericMethod(allTypes);
        var provider = getProviderMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
        var func = Delegate.CreateDelegate(delegateType, instance, method);
        
        providerType.GetMethod("RegisterFunc")!.Invoke(provider, new object[] { func });
        DisposalActions.Add(() =>
        {
            if (provider is ICallGateProvider baseProvider)
                baseProvider.UnregisterFunc();
        });
    }

    private static object? CreateSubscriber(string name, Type delegateType)
    {
        if (delegateType == typeof(Action))
        {
            var provider = Dalamud.PluginInterface.GetIpcSubscriber<object>(name);
            return new Action(() => provider.InvokeAction());
        }

        if (!delegateType.IsGenericType)
            return null;

        var genericDef = delegateType.GetGenericTypeDefinition();
        var genericArgs = delegateType.GetGenericArguments();

        if (genericDef == typeof(Action<>))
        {
            var t1 = genericArgs[0];
            var providerType = typeof(ICallGateSubscriber<,>).MakeGenericType(t1, typeof(object));
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(t1, typeof(object));
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeAction");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        if (genericDef == typeof(Action<,>))
        {
            var types = genericArgs.Concat(new[] { typeof(object) }).ToArray();
            var providerType = typeof(ICallGateSubscriber<,,>).MakeGenericType(types);
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == types.Length)
                .MakeGenericMethod(types);
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeAction");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        if (genericDef == typeof(Func<>))
        {
            var tRet = genericArgs[0];
            var providerType = typeof(ICallGateSubscriber<>).MakeGenericType(tRet);
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == 1)
                .MakeGenericMethod(tRet);
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeFunc");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        if (genericDef == typeof(Func<,>))
        {
            var t1 = genericArgs[0];
            var tRet = genericArgs[1];
            var providerType = typeof(ICallGateSubscriber<,>).MakeGenericType(t1, tRet);
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(t1, tRet);
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeFunc");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        if (genericDef == typeof(Func<,,>))
        {
            var t1 = genericArgs[0];
            var t2 = genericArgs[1];
            var tRet = genericArgs[2];
            var providerType = typeof(ICallGateSubscriber<,,>).MakeGenericType(t1, t2, tRet);
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == 3)
                .MakeGenericMethod(t1, t2, tRet);
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeFunc");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        if (genericDef == typeof(Func<,,,>))
        {
            var t1 = genericArgs[0];
            var t2 = genericArgs[1];
            var t3 = genericArgs[2];
            var tRet = genericArgs[3];
            var providerType = typeof(ICallGateSubscriber<,,,>).MakeGenericType(t1, t2, t3, tRet);
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == 4)
                .MakeGenericMethod(t1, t2, t3, tRet);
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeFunc");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        if (genericDef == typeof(Func<,,,,>))
        {
            var t1 = genericArgs[0];
            var t2 = genericArgs[1];
            var t3 = genericArgs[2];
            var t4 = genericArgs[3];
            var tRet = genericArgs[4];
            var providerType = typeof(ICallGateSubscriber<,,,,>).MakeGenericType(t1, t2, t3, t4, tRet);
            var getSubMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcSubscriber" && m.GetGenericArguments().Length == 5)
                .MakeGenericMethod(t1, t2, t3, t4, tRet);
            var provider = getSubMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var invokeMethod = providerType.GetMethod("InvokeFunc");
            return Delegate.CreateDelegate(delegateType, provider, invokeMethod!);
        }

        return null;
    }

    private static object? CreateEventProvider(string name, Type delegateType)
    {
        if (delegateType == typeof(Action))
        {
            var provider = Dalamud.PluginInterface.GetIpcProvider<object>(name);
            return new Action(() => provider.SendMessage());
        }

        if (!delegateType.IsGenericType)
            return null;

        var genericDef = delegateType.GetGenericTypeDefinition();
        var genericArgs = delegateType.GetGenericArguments();

        if (genericDef == typeof(Action<>))
        {
            var t1 = genericArgs[0];
            var providerType = typeof(ICallGateProvider<,>).MakeGenericType(t1, typeof(object));
            var getProviderMethod = typeof(IDalamudPluginInterface)
                .GetMethods()
                .First(m => m.Name == "GetIpcProvider" && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(t1, typeof(object));
            var provider = getProviderMethod.Invoke(Dalamud.PluginInterface, new object[] { name });
            var sendMessageMethod = providerType.GetMethod("SendMessage");
            return Delegate.CreateDelegate(delegateType, provider, sendMessageMethod!);
        }

        return null;
    }
}
