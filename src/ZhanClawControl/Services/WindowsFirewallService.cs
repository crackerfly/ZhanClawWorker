#nullable disable warnings
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZhanClawControl.Services;

public sealed class WindowsFirewallService
{
	private const int NetFwProfilePrivate = 2;

	private const int NetFwIpProtocolAny = 256;

	private const int NetFwRuleDirectionIn = 1;

	private const int NetFwActionAllow = 1;

	private const int MaximumRemovalPasses = 1024;

	public FirewallRuleInspection Inspect()
	{
		if (!OperatingSystem.IsWindows())
		{
			return new FirewallRuleInspection(Exists: false, MatchesExpectedDefinition: false, Array.Empty<string>(), QueryFailed: true, "Windows Firewall COM 仅在 Windows 上可用。");
		}
		try
		{
			List<FirewallRuleSnapshot> list = WithRules(new Func<object, List<FirewallRuleSnapshot>>(ReadProductRules));
			if (list.Count == 0)
			{
				return new FirewallRuleInspection(Exists: false, MatchesExpectedDefinition: false, new string[1] { "产品专用网络入站规则不存在。" });
			}
			List<string> list2 = new List<string>();
			if (list.Count != 1)
			{
				list2.Add($"固定规则名对应 {list.Count} 条规则，要求精确为 1 条。");
			}
			if (list.Count > 0)
			{
				ValidateSnapshot(list[0], list2);
			}
			return new FirewallRuleInspection(Exists: true, list2.Count == 0, list2);
		}
		catch (Exception ex)
		{
			return new FirewallRuleInspection(Exists: true, MatchesExpectedDefinition: false, new string[1] { "Windows Firewall 规则查询失败，无法证明其不存在或安全。" }, QueryFailed: true, DescribeException(ex));
		}
	}

	public void EnsureExpectedRule(CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		WithRules(delegate(dynamic rules)
		{
			RemoveAllProductRules(rules, ct);
			ct.ThrowIfCancellationRequested();
			AddExpectedRule(rules);
			return 0;
		});
		FirewallRuleInspection firewallRuleInspection = Inspect();
		if (firewallRuleInspection.QueryFailed || !firewallRuleInspection.Exists || !firewallRuleInspection.MatchesExpectedDefinition)
		{
			throw new InvalidOperationException("Windows Firewall 规则创建后的精确定义复核失败：" + DescribeInspection(firewallRuleInspection));
		}
	}

	public void DeleteProductRule(CancellationToken ct = default(CancellationToken))
	{
		ct.ThrowIfCancellationRequested();
		WithRules(delegate(dynamic rules)
		{
			RemoveAllProductRules(rules, ct);
			return 0;
		});
		FirewallRuleInspection firewallRuleInspection = Inspect();
		if (firewallRuleInspection.QueryFailed || firewallRuleInspection.Exists)
		{
			throw new InvalidOperationException("Windows Firewall 规则删除后的不存在性复核失败：" + DescribeInspection(firewallRuleInspection));
		}
	}

	public void RestoreTrustedState(bool expectedRuleWasPresent, CancellationToken ct = default(CancellationToken))
	{
		if (expectedRuleWasPresent)
		{
			EnsureExpectedRule(ct);
		}
		else
		{
			DeleteProductRule(ct);
		}
	}

	private static void ValidateSnapshot(FirewallRuleSnapshot snapshot, ICollection<string> issues)
	{
		if (!PathsEqual(snapshot.ApplicationName, AppPaths.AgentExe))
		{
			issues.Add("ApplicationName 未精确指向安装目录 p2p-agent.exe。");
		}
		if (snapshot.Direction != 1)
		{
			issues.Add("Direction 不是 Inbound。");
		}
		if (snapshot.Action != 1)
		{
			issues.Add("Action 不是 Allow。");
		}
		if (snapshot.Profiles != 2)
		{
			issues.Add("Profiles 不是仅 Private。");
		}
		if (!snapshot.Enabled)
		{
			issues.Add("规则未启用。");
		}
		if (snapshot.Protocol != 256)
		{
			issues.Add("Protocol 不是 Any，无法同时覆盖 libp2p 与 mDNS。");
		}
		if (snapshot.EdgeTraversal)
		{
			issues.Add("EdgeTraversal 必须关闭。");
		}
		if (!IsWildcard(snapshot.LocalAddresses))
		{
			issues.Add("LocalAddresses 不是 Any。");
		}
		if (!IsWildcard(snapshot.RemoteAddresses))
		{
			issues.Add("RemoteAddresses 不是 Any。");
		}
		if (!string.Equals(snapshot.InterfaceTypes.Trim(), "All", StringComparison.OrdinalIgnoreCase))
		{
			issues.Add("InterfaceTypes 不是 All。");
		}
		if (!string.Equals(snapshot.Grouping, "StarSoftComm ZhanClaw", StringComparison.Ordinal))
		{
			issues.Add("Grouping 不是固定产品组。");
		}
		if (!string.IsNullOrWhiteSpace(snapshot.ServiceName))
		{
			issues.Add("规则不应绑定 Windows 服务。");
		}
		if (snapshot.HasSpecificInterfaces)
		{
			issues.Add("规则不应限制到特定接口实例。");
		}
	}

	private static List<FirewallRuleSnapshot> ReadProductRules(dynamic rules)
	{
		List<FirewallRuleSnapshot> list = new List<FirewallRuleSnapshot>();
		IEnumerator enumerator = null;
		try
		{
			enumerator = ((IEnumerable)rules).GetEnumerator();
			while (enumerator.MoveNext())
			{
				object current = enumerator.Current;
				try
				{
					dynamic val = current;
					if (!((!string.Equals(Convert.ToString(val.Name, CultureInfo.InvariantCulture), "StarSoftComm ZhanClaw P2P Agent - Private Inbound", StringComparison.Ordinal)) ? true : false))
					{
						list.Add(new FirewallRuleSnapshot(Convert.ToString(val.ApplicationName, CultureInfo.InvariantCulture) ?? "", Convert.ToInt32(val.Direction, CultureInfo.InvariantCulture), Convert.ToInt32(val.Action, CultureInfo.InvariantCulture), Convert.ToInt32(val.Profiles, CultureInfo.InvariantCulture), Convert.ToBoolean(val.Enabled, CultureInfo.InvariantCulture), Convert.ToInt32(val.Protocol, CultureInfo.InvariantCulture), Convert.ToBoolean(val.EdgeTraversal, CultureInfo.InvariantCulture), Convert.ToString(val.LocalAddresses, CultureInfo.InvariantCulture) ?? "", Convert.ToString(val.RemoteAddresses, CultureInfo.InvariantCulture) ?? "", Convert.ToString(val.InterfaceTypes, CultureInfo.InvariantCulture) ?? "", Convert.ToString(val.Grouping, CultureInfo.InvariantCulture) ?? "", Convert.ToString(val.ServiceName, CultureInfo.InvariantCulture) ?? "", WindowsFirewallService.HasSpecificInterfaces(val.Interfaces)));
					}
				}
				finally
				{
					ReleaseCom(current);
				}
			}
			return list;
		}
		finally
		{
			if (enumerator is IDisposable disposable)
			{
				disposable.Dispose();
			}
			ReleaseCom(enumerator);
		}
	}

	private static bool HasSpecificInterfaces(object? value)
	{
		if (value == null || value is DBNull)
		{
			return false;
		}
		if (value is Array array)
		{
			return array.Length > 0;
		}
		return true;
	}

	private static void RemoveAllProductRules(dynamic rules, CancellationToken ct)
	{
		for (int i = 0; i < 1024; i++)
		{
			if (WindowsFirewallService.ReadProductRules(rules).Count == 0)
			{
				return;
			}
			ct.ThrowIfCancellationRequested();
			rules.Remove("StarSoftComm ZhanClaw P2P Agent - Private Inbound");
		}
		throw new InvalidOperationException("同名 Windows Firewall 规则数量异常，拒绝继续修改。");
	}

	private static void AddExpectedRule(dynamic rules)
	{
		Type type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: false) ?? throw new PlatformNotSupportedException("HNetCfg.FWRule COM 类型不可用。");
		object obj = null;
		try
		{
			obj = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建 Windows Firewall 规则对象。");
			dynamic val = obj;
			val.Name = "StarSoftComm ZhanClaw P2P Agent - Private Inbound";
			val.Grouping = "StarSoftComm ZhanClaw";
			val.Description = "Allows p2p-agent inbound traffic on private Windows networks.";
			val.ApplicationName = AppPaths.AgentExe;
			val.Protocol = 256;
			val.Direction = 1;
			val.Profiles = 2;
			val.Action = 1;
			val.Enabled = true;
			val.EdgeTraversal = false;
			val.LocalAddresses = "*";
			val.RemoteAddresses = "*";
			val.InterfaceTypes = "All";
			rules.Add(val);
		}
		finally
		{
			ReleaseCom(obj);
		}
	}

	private static T WithRules<T>(Func<dynamic, T> action)
	{
		Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false) ?? throw new PlatformNotSupportedException("HNetCfg.FwPolicy2 COM 类型不可用。");
		object obj = null;
		dynamic val = null;
		try
		{
			obj = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建 Windows Firewall policy 对象。");
			dynamic val2 = obj;
			val = val2.Rules;
			return ((Func<object, T>)action)(val);
		}
		finally
		{
			ReleaseCom((object?)val);
			ReleaseCom(obj);
		}
	}

	private static bool PathsEqual(string left, string right)
	{
		if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
		{
			return false;
		}
		try
		{
			return string.Equals(Path.GetFullPath(left.Trim().Trim('"')).TrimEnd('\\'), Path.GetFullPath(right.Trim().Trim('"')).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsWildcard(string value)
	{
		return string.Equals(value.Trim(), "*", StringComparison.Ordinal);
	}

	private static string DescribeInspection(FirewallRuleInspection inspection)
	{
		return string.Join("；", from value in inspection.Issues.Append(inspection.QueryError)
			where !string.IsNullOrWhiteSpace(value)
			select value);
	}

	private static string DescribeException(Exception ex)
	{
		Exception ex2;
		if (ex is TargetInvocationException)
		{
			Exception innerException = ex.InnerException;
			if (innerException != null)
			{
				ex2 = innerException;
				goto IL_0016;
			}
		}
		ex2 = ex;
		goto IL_0016;
		IL_0016:
		Exception ex3 = ex2;
		if (!(ex3 is COMException ex4))
		{
			return ex3.Message;
		}
		return $"{ex4.Message} (HRESULT=0x{ex4.HResult:X8})";
	}

	private static void ReleaseCom(object? value)
	{
		if (value != null && Marshal.IsComObject(value))
		{
			try
			{
				Marshal.FinalReleaseComObject(value);
			}
			catch
			{
			}
		}
	}
}
