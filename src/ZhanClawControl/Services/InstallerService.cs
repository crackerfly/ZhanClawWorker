#nullable disable warnings
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZhanClawControl.Services;

public sealed class InstallerService
{
	private sealed record RuntimeIdentitySnapshot(string PeerId, string AgentVersion, string IdentitySha256, string TokenSha256);

	public sealed record InterruptedUninstallRecovery(bool RemoveData, bool CanContinue, bool CanRollback, string Phase);

	private sealed record UninstallRecoveryState(int Schema, string Phase, bool RemoveData, bool DataRootWasPresent, string RuntimeUser, bool TaskWasPresent, bool TaskWasEnabled, bool WasRunning, bool FirewallRuleWasPresent, bool TaskMaintenanceMarkerPreexisted, string? ExpectedPeerId, string? ExpectedAgentVersion, string? ExpectedIdentitySha256, string? ExpectedTokenSha256);

	private enum BackupFileRole
	{
		Agent,
		Control,
		Config,
		Swarm,
		Identity,
		Token,
		Journal,
		IdentityProvisioningMarker,
		TokenProvisioningMarker
	}

	private sealed class StopCaptureRecoveredException : InvalidOperationException
	{
		public StopCaptureRecoveredException(string message, Exception? inner = null)
			: base(message, inner)
		{
		}
	}

	private sealed class StopCaptureRecoveryFailedException : InvalidOperationException
	{
		public StopCaptureRecoveryFailedException(string message, Exception? inner = null)
			: base(message, inner)
		{
		}
	}

	private sealed class DeploymentBackup
	{
		private sealed record BackupEntry(string Path, string Sha256, long Length);

		private sealed record BackupSlot(string Target, BackupFileRole Role, bool RestoreAllowed, BackupEntry? Entry);

		private sealed class JournalPreflight : IAsyncDisposable
		{
			public FileStream? Lease { get; }

			public string? Sha256 { get; }

			public long Length { get; }

			public JournalPreflight(FileStream? lease, string? sha256, long length)
			{
				Lease = lease;
				Sha256 = sha256;
				Length = length;
			}

			public ValueTask DisposeAsync()
			{
				if (Lease != null)
				{
					return Lease.DisposeAsync();
				}
				return ValueTask.CompletedTask;
			}
		}

		private readonly string _root;

		private readonly string _runtimeUser;

		private readonly RuntimeIdentitySnapshot? _expectedRuntime;

		private readonly List<BackupSlot> _files = new List<BackupSlot>();

		private long _capturedBytes;

		private bool _runtimeStartAttempted;

		private bool _retainForRecovery;

		public string? TaskXml { get; }

		public bool TaskWasEnabled { get; }

		public bool FirewallRuleWasPresent { get; }

		public string RuntimeUser => _runtimeUser;

		public RuntimeIdentitySnapshot? ExpectedRuntime => _expectedRuntime;

		public string JournalRecoveryDetail { get; private set; } = "journal 与停机快照等价，未执行倒退覆盖";

		public string RootPath => _root;

		public DeploymentBackup(string? taskXml, bool taskWasEnabled, bool firewallRuleWasPresent, string runtimeUser, RuntimeIdentitySnapshot? expectedRuntime, string? fixedRoot = null)
		{
			TaskXml = taskXml;
			TaskWasEnabled = taskWasEnabled;
			FirewallRuleWasPresent = firewallRuleWasPresent;
			_runtimeUser = runtimeUser;
			_expectedRuntime = expectedRuntime;
			_root = fixedRoot ?? Path.Combine("C:\\Program Files\\P2PAgent", $".install-rollback-{Guid.NewGuid():N}");
			if (Directory.Exists(_root) || File.Exists(_root))
			{
				throw new IOException("回滚目录已存在，拒绝覆盖：" + _root);
			}
			RuntimeSecurityService.PrepareSecureRollbackDirectory(_root);
			RuntimeSecurityService.ProtectRollbackTree(_root);
		}

		public async Task CaptureAsync(string path, BackupFileRole role, bool restoreAllowed, CancellationToken ct)
		{
			if (_files.Any((BackupSlot slot) => string.Equals(slot.Target, path, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("回滚点包含重复目标：" + path);
			}
			FileStream fileStream;
			try
			{
				if (IsProtectedRuntimePath(path))
				{
					fileStream = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(path);
				}
				else
				{
					if (!File.Exists(path))
					{
						if (Directory.Exists(path))
						{
							throw new IOException("恢复材料文件路径被目录占用：" + path);
						}
						_files.Add(new BackupSlot(path, role, restoreAllowed, null));
						return;
					}
					fileStream = OpenRecoverySource(path);
				}
			}
			catch (Exception ex) when (((Func<bool>)delegate
			{
				// Could not convert BlockContainer to single expression
				bool flag = IsProtectedRuntimePath(path);
				if (flag)
				{
					flag = ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false);
				}
				return flag;
			}).Invoke())
			{
				_files.Add(new BackupSlot(path, role, restoreAllowed, null));
				return;
			}
			await using (fileStream)
			{
				string backupPath = Path.Combine(_root, $"{_files.Count:D2}-{role.ToString().ToLowerInvariant()}.bak");
				BackupEntry entry = await CopyIntoBackupAsync(fileStream, path, backupPath, ct).ConfigureAwait(continueOnCapturedContext: false);
				_files.Add(new BackupSlot(path, role, restoreAllowed, entry));
			}
		}

		public void ValidateExpectedRuntimeSnapshot()
		{
			if ((object)_expectedRuntime != null)
			{
				BackupEntry? obj = GetSlot(BackupFileRole.Identity).Entry ?? throw new InvalidDataException("运行态身份快照缺少 agent-identity.key。 ");
				BackupEntry backupEntry = GetSlot(BackupFileRole.Token).Entry ?? throw new InvalidDataException("运行态身份快照缺少 agent-api.token。 ");
				if (!string.Equals(obj.Sha256, _expectedRuntime.IdentitySha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("停机后 identity 哈希与停机前运行态不一致，拒绝建立回滚点。");
				}
				if (!string.Equals(backupEntry.Sha256, _expectedRuntime.TokenSha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("停机后 Token 哈希与停机前运行态不一致，拒绝建立回滚点。");
				}
			}
		}

		public async Task ValidatePreservedIdentityAndTokenAsync(CancellationToken ct, bool preserveIdentity = true, bool preserveToken = true)
		{
			BackupFileRole[] array = new BackupFileRole[2]
			{
				BackupFileRole.Identity,
				BackupFileRole.Token
			};
			foreach (BackupFileRole role in array)
			{
				if ((role == BackupFileRole.Identity && !preserveIdentity) || (role == BackupFileRole.Token && !preserveToken))
				{
					continue;
				}
				BackupSlot slot = GetSlot(role);
				if ((object)slot.Entry != null)
				{
					(string, long) tuple = await HashFileBoundedAsync(slot.Target, ct).ConfigureAwait(continueOnCapturedContext: false);
					if (tuple.Item2 != slot.Entry.Length || !string.Equals(tuple.Item1, slot.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException($"Agent 启动后 {role} 与停机快照的长度或 SHA-256 不一致。");
					}
				}
			}
		}

		public async Task ValidateCapturedTargetsStillMatchAsync(CancellationToken ct)
		{
			foreach (BackupSlot slot in _files.Where(delegate(BackupSlot backupSlot)
			{
				BackupFileRole role = backupSlot.Role;
				return (uint)(role - 2) <= 6u;
			}))
			{
				if (!(await TargetMatchesSnapshotAsync(slot, ct).ConfigureAwait(continueOnCapturedContext: false)))
				{
					throw new IOException("敏感运行文件在停机快照完成前发生替换或内容变化：" + slot.Target);
				}
			}
		}

		public async Task WriteRecoveryManifestAsync(CancellationToken ct)
		{
			string taskFile = null;
			string taskSha256 = null;
			if (TaskXml != null)
			{
				byte[] bytes = Encoding.UTF8.GetBytes(TaskXml);
				if (bytes.LongLength > 4194304)
				{
					throw new IOException("计划任务 XML 超过 4 MiB 恢复材料上限。");
				}
				EnsureTotalCapacity(bytes.LongLength, "计划任务 XML");
				taskFile = "task.xml";
				string taskPath = Path.Combine(_root, taskFile);
				await WriteDurableFileAsync(taskPath, bytes, ct).ConfigureAwait(continueOnCapturedContext: false);
				taskSha256 = Convert.ToHexString(SHA256.HashData(bytes));
				(string, long) tuple = await HashFileBoundedAsync(taskPath, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (tuple.Item2 != bytes.LongLength || !string.Equals(tuple.Item1, taskSha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("计划任务 XML 写入恢复目录后的长度或 SHA-256 复核失败。");
				}
				_capturedBytes += bytes.LongLength;
			}
			byte[] bytes2 = JsonSerializer.SerializeToUtf8Bytes(new
			{
				schema = 1,
				captured_utc = DateTimeOffset.UtcNow,
				protection = "BA/SY-only",
				consistency = "captured only after confirmed Agent stop",
				limits = new
				{
					per_file_bytes = 536870912L,
					total_bytes = 1073741824L
				},
				task = new
				{
					present = (TaskXml != null),
					enabled = TaskWasEnabled,
					file = taskFile,
					sha256 = taskSha256
				},
				firewall_rule_was_present = FirewallRuleWasPresent,
				runtime_user = _runtimeUser,
				expected_runtime = (object)(((object)_expectedRuntime == null) ? null : new
				{
					peer_id = _expectedRuntime.PeerId,
					agent_version = _expectedRuntime.AgentVersion,
					identity_sha256 = _expectedRuntime.IdentitySha256,
					token_sha256 = _expectedRuntime.TokenSha256
				}),
				journal_policy = "never replace a later current journal with this stopped snapshot",
				files = _files.Select((BackupSlot slot) => new
				{
					target = slot.Target,
					role = slot.Role.ToString(),
					present = ((object)slot.Entry != null),
					backup_file = (((object)slot.Entry == null) ? null : Path.GetFileName(slot.Entry.Path)),
					sha256 = slot.Entry?.Sha256,
					length = (slot.Entry?.Length ?? 0),
					restore_allowed = slot.RestoreAllowed
				}).ToArray()
			}, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			await WriteDurableFileAsync(Path.Combine(_root, "recovery-manifest.json"), bytes2, ct).ConfigureAwait(continueOnCapturedContext: false);
		}

		public void MarkRuntimeStartAttempted()
		{
			if (!_runtimeStartAttempted)
			{
				string path = Path.Combine(_root, "runtime-start-attempted.marker");
				byte[] bytes = Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O"));
				using (FileStream fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
				{
					fileStream.Write(bytes);
					fileStream.Flush(flushToDisk: true);
				}
				RuntimeSecurityService.ProtectRollbackTree(_root);
				_runtimeStartAttempted = true;
			}
		}

		public async Task RestoreAsync(bool requireRunnableDeployment, CancellationToken ct)
		{
			RuntimeSecurityService.ProtectRollbackTree(_root);
			foreach (BackupSlot slot in _files.Where((BackupSlot backupSlot) => (object)backupSlot.Entry != null))
			{
				(string, long) tuple = await HashFileBoundedAsync(slot.Entry.Path, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (tuple.Item2 != slot.Entry.Length || !string.Equals(tuple.Item1, slot.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("回滚备份在恢复前长度或 SHA-256 不匹配：" + slot.Entry.Path);
				}
			}
			string[] array = (from backupSlot in _files
				where !backupSlot.RestoreAllowed && ((object)backupSlot.Entry != null || requireRunnableDeployment)
				select backupSlot.Role.ToString()).ToArray();
			if (array.Length != 0)
			{
				_retainForRecovery = true;
				throw new InvalidDataException("停机快照包含只能人工处理、不得自动写回的对象：" + string.Join(", ", array) + "。已在覆盖任何文件前中止回滚并保留 BA/SY-only 恢复材料。");
			}
			await using JournalPreflight currentJournal = await OpenCurrentJournalAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			BackupSlot slot2 = GetSlot(BackupFileRole.Journal);
			bool flag = JournalMatchesSnapshot(slot2, currentJournal);
			if ((object)slot2.Entry != null && currentJournal.Lease == null)
			{
				_retainForRecovery = true;
				JournalRecoveryDetail = "当前 journal 在停机快照后缺失；没有证据证明可安全倒回，停机快照仅保留作人工恢复材料";
				throw new InvalidDataException(JournalRecoveryDetail);
			}
			if (!flag)
			{
				_retainForRecovery = true;
				bool payloadEquivalent = await TargetMatchesSnapshotAsync(GetSlot(BackupFileRole.Agent), ct).ConfigureAwait(continueOnCapturedContext: false);
				bool num = await TargetMatchesSnapshotAsync(GetSlot(BackupFileRole.Identity), ct).ConfigureAwait(continueOnCapturedContext: false);
				JournalRecoveryDetail = "检测到停机快照之后的更新 journal；自动回滚保留当前最新 journal，旧快照保留在受保护恢复目录";
				if (!payloadEquivalent)
				{
					JournalRecoveryDetail += "；新旧 Agent 载荷 SHA-256 不同，无法证明 journal schema 等价，已在覆盖任何文件前中止自动回滚";
					throw new InvalidDataException(JournalRecoveryDetail);
				}
				if (!num)
				{
					JournalRecoveryDetail += "；当前 identity 也已变化，不能把较新 journal 与旧身份混合，已在覆盖任何文件前中止自动回滚";
					throw new InvalidDataException(JournalRecoveryDetail);
				}
			}
			else
			{
				JournalRecoveryDetail = ((_runtimeStartAttempted || File.Exists(Path.Combine(_root, "runtime-start-attempted.marker"))) ? "新实例虽已尝试启动，但当前 journal 的长度与 SHA-256 仍等同停机快照；未执行覆盖" : "新实例未启动，当前 journal 的长度与 SHA-256 等同停机快照；未执行覆盖");
			}
			RuntimeSecurityService.EnsureSafeInstallRoot();
			RuntimeSecurityService.PrepareSecureDataRoot(_runtimeUser);
			foreach (BackupSlot item in _files.Where((BackupSlot backupSlot) => backupSlot.Role != BackupFileRole.Journal))
			{
				ct.ThrowIfCancellationRequested();
				await RestoreSlotAsync(item, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			RuntimeSecurityService.EnsureSafeInstallRoot();
			RuntimeSecurityService.PrepareSecureDataRoot(_runtimeUser);
			foreach (string item2 in new string[5]
			{
				AppPaths.ConfigFile,
				AppPaths.SwarmKeyFile,
				AppPaths.IdentityFile,
				AppPaths.ApiTokenFile,
				AppPaths.JournalFile
			}.Where(File.Exists))
			{
				RuntimeSecurityService.ProtectAndValidateRuntimeFile(item2, _runtimeUser);
			}
			RuntimeSecurityService.ValidateExistingDataRootTrust(_runtimeUser);
			RuntimeSecurityService.ValidateSecureDataRootForWrite();
			foreach (BackupSlot item3 in _files.Where((BackupSlot backupSlot) => backupSlot.Role != BackupFileRole.Journal))
			{
				await ValidateRestoredSlotAsync(item3, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			await ValidateCurrentJournalStillMatchesAsync(currentJournal, ct).ConfigureAwait(continueOnCapturedContext: false);
			BackupSlot slot3 = GetSlot(BackupFileRole.Agent);
			BackupSlot slot4 = GetSlot(BackupFileRole.Config);
			BackupSlot slot5 = GetSlot(BackupFileRole.Swarm);
			if (requireRunnableDeployment && (!slot3.RestoreAllowed || !slot4.RestoreAllowed || !slot5.RestoreAllowed))
			{
				throw new InvalidDataException("原计划任务存在，但其 Agent、配置或 swarm.key 停机快照不满足可信启动门禁。");
			}
			if (File.Exists(AppPaths.AgentExe) && slot3.RestoreAllowed)
			{
				RuntimeSecurityService.ValidateTrustedAgentPublisherForRollback(AppPaths.AgentExe);
			}
			if (File.Exists(AppPaths.ConfigFile) && slot4.RestoreAllowed)
			{
				AgentConfigService.ValidateRuntimeBoundary(new AgentConfigService().Load());
			}
			if (File.Exists(AppPaths.SwarmKeyFile) && slot5.RestoreAllowed)
			{
				RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
			}
			if (requireRunnableDeployment && (!File.Exists(AppPaths.AgentExe) || !File.Exists(AppPaths.ControlExe) || !File.Exists(AppPaths.ConfigFile) || !File.Exists(AppPaths.SwarmKeyFile)))
			{
				throw new InvalidDataException("原计划任务存在，但恢复后的可运行部署不完整。");
			}
		}

		private async Task<BackupEntry> CopyIntoBackupAsync(FileStream source, string sourcePath, string backupPath, CancellationToken ct)
		{
			_ = 5;
			try
			{
				long expectedLength = source.Length;
				if (expectedLength > 536870912)
				{
					throw new IOException($"单文件超过 {512} MiB 恢复材料上限（{expectedLength} 字节）。");
				}
				EnsureTotalCapacity(expectedLength, sourcePath);
				EnsureFreeSpace(expectedLength, sourcePath);
				BackupEntry result;
				await using (FileStream destination = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.SequentialScan))
				{
					using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					byte[] buffer = new byte[131072];
					long copied = 0L;
					while (true)
					{
						int num = await source.ReadAsync(buffer, ct).ConfigureAwait(continueOnCapturedContext: false);
						if (num == 0)
						{
							break;
						}
						copied = checked(copied + num);
						if (copied > 536870912 || copied > expectedLength)
						{
							throw new IOException("源文件在停机快照期间增长或超过单文件容量上限。");
						}
						hash.AppendData(buffer, 0, num);
						await destination.WriteAsync(buffer.AsMemory(0, num), ct).ConfigureAwait(continueOnCapturedContext: false);
					}
					await destination.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
					destination.Flush(flushToDisk: true);
					if (copied != expectedLength || source.Length != expectedLength)
					{
						throw new IOException("源文件在停机快照期间长度发生变化。");
					}
					string sha256 = Convert.ToHexString(hash.GetHashAndReset());
					_capturedBytes += copied;
					await destination.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
					(string, long) tuple = await HashFileBoundedAsync(backupPath, ct).ConfigureAwait(continueOnCapturedContext: false);
					if (tuple.Item2 != copied || !string.Equals(tuple.Item1, sha256, StringComparison.OrdinalIgnoreCase))
					{
						throw new IOException("写入恢复目录后的长度或 SHA-256 复核失败。");
					}
					result = new BackupEntry(backupPath, sha256, copied);
				}
				return result;
			}
			catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
			{
				TryDelete(backupPath);
				throw new IOException($"流式捕获恢复材料失败：{sourcePath}；已捕获 {_capturedBytes} 字节，事务总上限 {1073741824} 字节；{ex.Message}", ex);
			}
		}

		private async Task RestoreSlotAsync(BackupSlot slot, CancellationToken ct)
		{
			if ((object)slot.Entry == null)
			{
				DeleteTargetIfPresent(slot.Target);
				return;
			}
			string text = Path.GetDirectoryName(slot.Target) ?? throw new InvalidDataException("恢复目标缺少父目录：" + slot.Target);
			Directory.CreateDirectory(text);
			RuntimeSecurityService.RejectReparsePoint(text);
			if (Directory.Exists(slot.Target))
			{
				throw new IOException("恢复目标文件路径被目录占用：" + slot.Target);
			}
			if (File.Exists(slot.Target))
			{
				RuntimeSecurityService.RejectReparsePoint(slot.Target);
			}
			string temporary = Path.Combine(text, $".{Path.GetFileName(slot.Target)}.{Guid.NewGuid():N}.rollback");
			try
			{
				await CopyBackupToTemporaryAsync(slot.Entry, temporary, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (slot.Role == BackupFileRole.Agent)
				{
					RuntimeSecurityService.RestrictAgentExecutionForMaintenance(temporary);
				}
				File.Move(temporary, slot.Target, overwrite: true);
			}
			finally
			{
				TryDelete(temporary);
			}
		}

		private static async Task CopyBackupToTemporaryAsync(BackupEntry entry, string temporary, CancellationToken ct)
		{
			RuntimeSecurityService.RejectReparsePoint(entry.Path);
			await using FileStream source = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
			await using FileStream destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.SequentialScan);
			using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			byte[] buffer = new byte[131072];
			long copied = 0L;
			while (true)
			{
				int num = await source.ReadAsync(buffer, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (num == 0)
				{
					break;
				}
				copied = checked(copied + num);
				if (copied > entry.Length)
				{
					throw new IOException("回滚备份在恢复期间增长。");
				}
				hash.AppendData(buffer, 0, num);
				await destination.WriteAsync(buffer.AsMemory(0, num), ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			await destination.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			destination.Flush(flushToDisk: true);
			string a = Convert.ToHexString(hash.GetHashAndReset());
			if (copied != entry.Length || source.Length != entry.Length || !string.Equals(a, entry.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("回滚备份在流式恢复期间长度或 SHA-256 不匹配：" + entry.Path);
			}
		}

		private static void DeleteTargetIfPresent(string target)
		{
			if (Directory.Exists(target))
			{
				throw new IOException("恢复目标文件路径被目录占用：" + target);
			}
			if (File.Exists(target))
			{
				RuntimeSecurityService.RejectReparsePoint(target);
				File.Delete(target);
				if (File.Exists(target))
				{
					throw new IOException("无法删除事务中新建的文件：" + target);
				}
			}
		}

		private static async Task ValidateRestoredSlotAsync(BackupSlot slot, CancellationToken ct)
		{
			if ((object)slot.Entry == null)
			{
				if (File.Exists(slot.Target) || Directory.Exists(slot.Target))
				{
					throw new IOException("应恢复为不存在的目标仍然存在：" + slot.Target);
				}
				return;
			}
			(string, long) tuple = await HashFileBoundedAsync(slot.Target, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (tuple.Item2 == slot.Entry.Length && string.Equals(tuple.Item1, slot.Entry.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			throw new InvalidDataException("恢复后的文件长度或 SHA-256 不匹配：" + slot.Target);
		}

		private static async Task<bool> TargetMatchesSnapshotAsync(BackupSlot slot, CancellationToken ct)
		{
			try
			{
				(string, long) tuple = await HashFileBoundedAsync(slot.Target, ct).ConfigureAwait(continueOnCapturedContext: false);
				return (object)slot.Entry != null && tuple.Item2 == slot.Entry.Length && string.Equals(tuple.Item1, slot.Entry.Sha256, StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
			{
				return (object)slot.Entry == null;
			}
		}

		private static bool JournalMatchesSnapshot(BackupSlot slot, JournalPreflight current)
		{
			if ((object)slot.Entry == null)
			{
				return current.Lease == null;
			}
			if (current.Lease != null && current.Length == slot.Entry.Length)
			{
				return string.Equals(current.Sha256, slot.Entry.Sha256, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static async Task<JournalPreflight> OpenCurrentJournalAsync(CancellationToken ct)
		{
			FileStream stream = null;
			try
			{
				stream = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(AppPaths.JournalFile);
				long length = stream.Length;
				if (length > 536870912)
				{
					throw new IOException($"当前 journal 超过 {512} MiB 自动回滚核验上限。");
				}
				string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false));
				if (stream.Length != length)
				{
					throw new IOException("当前 journal 在回滚核验期间长度发生变化。");
				}
				return new JournalPreflight(stream, sha, length);
			}
			catch (Exception ex) when (((Func<bool>)delegate
			{
				// Could not convert BlockContainer to single expression
				bool flag = stream == null;
				if (flag)
				{
					flag = ((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? true : false);
				}
				return flag;
			}).Invoke())
			{
				return new JournalPreflight(null, null, 0L);
			}
			catch
			{
				if (stream != null)
				{
					await stream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
				}
				throw;
			}
		}

		private static async Task ValidateCurrentJournalStillMatchesAsync(JournalPreflight current, CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			if (current.Lease == null)
			{
				try
				{
					using (RuntimeSecurityService.OpenProtectedRuntimeFileForRead(AppPaths.JournalFile))
					{
						throw new IOException("回滚期间出现新的 journal，已拒绝把它当作停机快照覆盖或忽略。");
					}
				}
				catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
				{
					return;
				}
			}
			if (current.Lease.Length != current.Length)
			{
				throw new IOException("回滚期间当前 journal 长度发生变化。");
			}
			current.Lease.Position = 0L;
			string a = Convert.ToHexString(await SHA256.HashDataAsync(current.Lease, ct).ConfigureAwait(continueOnCapturedContext: false));
			if (current.Lease.Length != current.Length || !string.Equals(a, current.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("回滚期间当前 journal 内容发生变化。");
			}
			using FileStream finalPath = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(AppPaths.JournalFile);
			long finalLength = finalPath.Length;
			string a2 = Convert.ToHexString(await SHA256.HashDataAsync(finalPath, ct).ConfigureAwait(continueOnCapturedContext: false));
			if (finalLength != current.Length || finalPath.Length != finalLength || !string.Equals(a2, current.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new IOException("回滚期间 journal 最终路径发生替换或内容变化。");
			}
		}

		private BackupSlot GetSlot(BackupFileRole role)
		{
			return _files.SingleOrDefault((BackupSlot slot) => slot.Role == role) ?? throw new InvalidDataException("回滚点缺少必要文件角色：" + role);
		}

		private void EnsureTotalCapacity(long additionalBytes, string source)
		{
			if (additionalBytes < 0 || additionalBytes > 1073741824 - _capturedBytes)
			{
				throw new IOException($"恢复材料总量将超过 {1024} MiB 上限：{source}");
			}
		}

		private void EnsureFreeSpace(long requiredBytes, string source)
		{
			try
			{
				string? pathRoot = Path.GetPathRoot(_root);
				if (string.IsNullOrWhiteSpace(pathRoot))
				{
					throw new IOException("无法确定恢复目录所在卷。");
				}
				long availableFreeSpace = new DriveInfo(pathRoot).AvailableFreeSpace;
				if (availableFreeSpace < requiredBytes + 16777216)
				{
					throw new IOException($"恢复目录所在卷可用空间不足：需要至少 {requiredBytes + 16777216} 字节，实际 {availableFreeSpace} 字节。");
				}
			}
			catch (IOException)
			{
				throw;
			}
			catch (Exception innerException)
			{
				throw new IOException("无法核验恢复材料磁盘容量：" + source, innerException);
			}
		}

		private static async Task WriteDurableFileAsync(string path, byte[] bytes, CancellationToken ct)
		{
			await using FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough | FileOptions.Asynchronous);
			await stream.WriteAsync(bytes, ct).ConfigureAwait(continueOnCapturedContext: false);
			await stream.FlushAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			stream.Flush(flushToDisk: true);
		}

		public bool Delete()
		{
			if (_retainForRecovery)
			{
				return false;
			}
			try
			{
				if (Directory.Exists(_root))
				{
					Directory.Delete(_root, recursive: true);
				}
				return !Directory.Exists(_root);
			}
			catch
			{
				return false;
			}
		}
	}

	private const long MaxRollbackFileBytes = 536870912L;

	private const long MaxRollbackTotalBytes = 1073741824L;

	private const int RollbackCopyBufferBytes = 131072;

	private const string UninstallPhasePrepared = "prepared";

	private const string UninstallPhaseSnapshotReady = "snapshot-ready";

	private const string UninstallPhaseCommitStarted = "commit-started";

	private const int MaxUninstallRecoveryStateBytes = 32768;

	private readonly AgentConfigService _config = new AgentConfigService();

	private readonly ScheduledTaskService _task = new ScheduledTaskService();

	private readonly WindowsFirewallService _firewall = new WindowsFirewallService();

	public static bool IsInstalled
	{
		get
		{
			if (!File.Exists(AppPaths.AgentExe) || !File.Exists(AppPaths.ConfigFile))
			{
				if (!File.Exists(AppPaths.ControlExe) || !File.Exists(AppPaths.TaskMaintenanceMarker))
				{
					return false;
				}
				try
				{
					if (!RuntimeSecurityService.TryReadTaskMaintenanceMarker(out var _))
					{
						return false;
					}
					return !RuntimeSecurityService.HasValidIdentityProvisioningMarker() || HasNonEmptyProtectedRuntimeFile(AppPaths.IdentityFile);
				}
				catch
				{
					return true;
				}
			}
			try
			{
				return HasNonEmptyProtectedRuntimeFile(AppPaths.IdentityFile) || !RuntimeSecurityService.HasValidIdentityProvisioningMarker();
			}
			catch
			{
				return true;
			}
		}
	}

	public static bool HasInterruptedUninstallArtifacts
	{
		get
		{
			if (!Directory.Exists(AppPaths.UninstallRecoveryRoot) && !File.Exists(AppPaths.UninstallRecoveryRoot) && !Directory.Exists(AppPaths.UninstallRecoveryStageRoot) && !File.Exists(AppPaths.UninstallRecoveryStageRoot) && !Directory.Exists(AppPaths.UninstallRecoveryCleanupRoot))
			{
				return File.Exists(AppPaths.UninstallRecoveryCleanupRoot);
			}
			return true;
		}
	}

	public static string CurrentUserName
	{
		get
		{
			try
			{
				return WindowsIdentity.GetCurrent().Name;
			}
			catch
			{
				return Environment.UserDomainName + "\\" + Environment.UserName;
			}
		}
	}

	public static bool HasEmbeddedSwarmKey => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("ZhanClawControl.payload.swarm.key");

	public static void CleanupInterruptedUninstallTombstones()
	{
		string[] array = new string[2]
		{
			AppPaths.UninstallRecoveryStageRoot,
			AppPaths.UninstallRecoveryCleanupRoot
		};
		foreach (string text in array)
		{
			if (File.Exists(text))
			{
				throw new InvalidDataException("卸载恢复固定目录路径被文件占用：" + text);
			}
			if (Directory.Exists(text))
			{
				RuntimeSecurityService.NormalizeProtectedRollbackTree(text);
				RuntimeSecurityService.DeleteProtectedRollbackTree(text);
			}
		}
	}

	public static async Task<IReadOnlyList<DeploymentIssue>> CheckDeploymentAsync(CancellationToken ct = default(CancellationToken))
	{
		List<DeploymentIssue> issues = new List<DeploymentIssue>();
		if (HasInterruptedUninstallArtifacts)
		{
			try
			{
				InterruptedUninstallRecovery interruptedUninstallRecovery = GetInterruptedUninstallRecovery();
				issues.Add(new DeploymentIssue("DeploymentUninstallInterrupted", $"phase={interruptedUninstallRecovery.Phase}; removeData={interruptedUninstallRecovery.RemoveData}"));
			}
			catch (Exception ex)
			{
				issues.Add(new DeploymentIssue("DeploymentUninstallRecoveryInvalid", ex.Message));
			}
		}
		if (!TryGetPendingControlSelfDelete(out bool pending, out string error))
		{
			issues.Add(new DeploymentIssue("DeploymentDeferredCleanupQueryFailed", error));
		}
		else if (pending)
		{
			issues.Add(new DeploymentIssue("DeploymentDeferredCleanupPending"));
		}
		if (!File.Exists(AppPaths.ControlExe))
		{
			issues.Add(new DeploymentIssue("DeploymentControlMissing"));
		}
		else
		{
			string processPath = Environment.ProcessPath;
			bool flag = !string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath);
			if (flag)
			{
				flag = !(await FilesHaveSameContentAsync(AppPaths.ControlExe, processPath, ct).ConfigureAwait(continueOnCapturedContext: false));
			}
			if (flag)
			{
				issues.Add(new DeploymentIssue("DeploymentControlHashMismatch"));
			}
		}
		ScheduledTaskInspection scheduledTaskInspection = await new ScheduledTaskService().InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (scheduledTaskInspection.QueryFailed)
		{
			issues.Add(new DeploymentIssue("DeploymentTaskQueryFailed", scheduledTaskInspection.QueryError));
		}
		else if (!scheduledTaskInspection.Exists)
		{
			issues.Add(new DeploymentIssue("DeploymentTaskMissing"));
		}
		else if (!scheduledTaskInspection.MatchesExpectedDefinition)
		{
			issues.AddRange(scheduledTaskInspection.Issues.Select((string issue) => new DeploymentIssue("DeploymentTaskDefinition", issue)));
		}
		bool num = scheduledTaskInspection.Exists || File.Exists(AppPaths.AgentExe) || File.Exists(AppPaths.ControlExe) || File.Exists(AppPaths.ConfigFile) || File.Exists(AppPaths.SwarmKeyFile) || File.Exists(AppPaths.IdentityFile) || File.Exists(AppPaths.ApiTokenFile) || File.Exists(AppPaths.JournalFile) || File.Exists(AppPaths.IdentityProvisioningMarker) || File.Exists(AppPaths.TokenProvisioningMarker);
		if (num && IsMissingOrEmptyForDeploymentReport(AppPaths.IdentityFile))
		{
			issues.Add(new DeploymentIssue("DeploymentIdentityMissing"));
		}
		if (num && IsMissingOrEmptyForDeploymentReport(AppPaths.ApiTokenFile))
		{
			issues.Add(new DeploymentIssue("DeploymentTokenMissing"));
		}
		try
		{
			if (RuntimeSecurityService.HasValidIdentityProvisioningMarker())
			{
				issues.Add(new DeploymentIssue(File.Exists(AppPaths.IdentityFile) ? "DeploymentIdentityProvisioningResidue" : "DeploymentIdentityProvisioningIncomplete"));
			}
		}
		catch
		{
			issues.Add(new DeploymentIssue("DeploymentIdentityProvisioningInvalid"));
		}
		try
		{
			if (RuntimeSecurityService.HasValidTokenProvisioningMarker())
			{
				issues.Add(new DeploymentIssue(File.Exists(AppPaths.ApiTokenFile) ? "DeploymentTokenProvisioningResidue" : "DeploymentTokenProvisioningIncomplete"));
			}
		}
		catch
		{
			issues.Add(new DeploymentIssue("DeploymentTokenProvisioningInvalid"));
		}
		try
		{
			if (RuntimeSecurityService.TryReadTaskMaintenanceMarker(out var desiredEnabled,
				out RuntimeSecurityService.TaskMaintenancePhase phase))
			{
				issues.Add(new DeploymentIssue(
					phase == RuntimeSecurityService.TaskMaintenancePhase.ValidationReady
						? "DeploymentTaskMaintenanceValidationReady"
						: "DeploymentTaskMaintenanceInterrupted",
					desiredEnabled ? "true" : "false"));
			}
		}
		catch (Exception ex2)
		{
			issues.Add(new DeploymentIssue("DeploymentTaskMaintenanceInvalid", ex2.Message));
		}
		if (File.Exists(AppPaths.TaskMaintenanceCleanupMarker) || Directory.Exists(AppPaths.TaskMaintenanceCleanupMarker))
		{
			issues.Add(new DeploymentIssue("DeploymentTaskMaintenanceCleanupPending"));
		}
		if (RuntimeSecurityService.HasMaintenanceStartPermitObject)
		{
			try
			{
				issues.Add(new DeploymentIssue("DeploymentMaintenanceStartPermitResidual", RuntimeSecurityService.ValidateMaintenanceStartPermitForDeployment()));
			}
			catch (Exception ex3)
			{
				issues.Add(new DeploymentIssue("DeploymentMaintenanceStartPermitInvalid", ex3.Message));
			}
		}
		FirewallRuleInspection firewallRuleInspection = new WindowsFirewallService().Inspect();
		if (firewallRuleInspection.QueryFailed)
		{
			issues.Add(new DeploymentIssue("DeploymentFirewallQueryFailed", firewallRuleInspection.QueryError));
		}
		else if (!firewallRuleInspection.Exists)
		{
			issues.Add(new DeploymentIssue("DeploymentFirewallMissing"));
		}
		else if (!firewallRuleInspection.MatchesExpectedDefinition)
		{
			issues.Add(new DeploymentIssue("DeploymentFirewallDefinition", string.Join("；", firewallRuleInspection.Issues)));
		}
		if (!File.Exists(AppPaths.AgentExe))
		{
			issues.Add(new DeploymentIssue("DeploymentAgentMissing"));
		}
		else
		{
			try
			{
				await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (RuntimeSecurityService.IsAgentExecutionRestricted(AppPaths.AgentExe))
				{
					issues.Add(new DeploymentIssue("DeploymentAgentExecutionRestricted"));
				}
			}
			catch (Exception ex4)
			{
				issues.Add(new DeploymentIssue("DeploymentAgentIntegrity", ex4.Message));
			}
		}
		if (!File.Exists(AppPaths.ConfigFile))
		{
			issues.Add(new DeploymentIssue("DeploymentConfigMissing"));
		}
		else
		{
			try
			{
				AgentConfigService.ValidateRuntimeBoundary(new AgentConfigService().Load());
			}
			catch (Exception ex5)
			{
				issues.Add(new DeploymentIssue("DeploymentConfigInvalid", ex5.Message));
			}
		}
		if (!File.Exists(AppPaths.SwarmKeyFile))
		{
			issues.Add(new DeploymentIssue("DeploymentSwarmMissing"));
		}
		else
		{
			try
			{
				RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
			}
			catch (Exception ex6)
			{
				issues.Add(new DeploymentIssue("DeploymentSwarmInvalid", ex6.Message));
			}
		}
		try
		{
			RuntimeSecurityService.ValidateSecureDataRootForWrite();
		}
		catch (Exception ex7)
		{
			issues.Add(new DeploymentIssue("DeploymentDataSecurityInvalid", ex7.Message));
		}
		return issues;
	}

	public async Task<IReadOnlyList<InstallStep>> RepairAsync(IProgress<InstallStep>? progress = null, CancellationToken ct = default(CancellationToken))
	{
		List<InstallStep> steps = new List<InstallStep>();
		if (HasInterruptedUninstallArtifacts)
		{
			Record("检查未完成卸载", success: false, "检测到未处理的卸载恢复状态；修复不会覆盖该状态。请重新启动管理器并选择继续或回滚卸载。", InstallStepKind.NoMutationFailure);
			return steps;
		}
		if (!TryGetPendingControlSelfDelete(out bool pending, out string error))
		{
			Record("检查重启后清理状态", success: false, "无法确认 Windows 待处理文件删除队列，修复已安全中止：" + error, InstallStepKind.NoMutationFailure);
			return steps;
		}
		if (pending)
		{
			Record("检查重启后清理状态", success: false, "控制程序仍在 Windows 的重启后删除队列中。请先重新启动 Windows，再重新安装；否则新文件也会在下次重启时被删除。", InstallStepKind.NoMutationFailure);
			return steps;
		}
		ScheduledTaskInspection inspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (inspection.QueryFailed)
		{
			Record("读取现有计划任务", success: false, "无法确认现有任务及运行账户，修复已安全中止：" + inspection.QueryError);
			return steps;
		}
		FirewallRuleInspection firewallRuleInspection = _firewall.Inspect();
		if (firewallRuleInspection.QueryFailed)
		{
			Record("读取现有 Windows Firewall 规则", success: false, "无法确认产品防火墙规则状态，修复已安全中止：" + firewallRuleInspection.QueryError, InstallStepKind.NoMutationFailure);
			return steps;
		}
		bool previousFirewallExpected = firewallRuleInspection.Exists && firewallRuleInspection.MatchesExpectedDefinition;
		string runAsUser = ((inspection.Exists && inspection.MatchesExpectedDefinition && !string.IsNullOrWhiteSpace(inspection.RunAsUser)) ? inspection.RunAsUser : App.InteractiveUserName);
		if (string.IsNullOrWhiteSpace(runAsUser))
		{
			Record("确定运行账户", success: false, "计划任务不存在，无法推断原运行账户。请由调用方显式提供交互用户账户后再修复。");
			return steps;
		}
		string previousTaskXml = ((inspection.Exists && inspection.MatchesExpectedDefinition) ? inspection.RawXml : null);
		TaskState taskState = await _task.GetStateAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		bool previousTaskEnabled = previousTaskXml != null && inspection.EffectiveEnabled;
		bool desiredTaskEnabled = previousTaskXml == null || previousTaskEnabled;
		bool taskMaintenanceMarkerPreexisted;
		try
		{
			taskMaintenanceMarkerPreexisted = RuntimeSecurityService.TryReadTaskMaintenanceMarker(out var desiredEnabled);
			if (taskMaintenanceMarkerPreexisted)
			{
				desiredTaskEnabled = desiredEnabled;
			}
		}
		catch (Exception ex)
		{
			Record("读取维护状态", success: false, "计划任务维护意图无法安全核验，修复已中止：" + ex.Message, InstallStepKind.NoMutationFailure);
			return steps;
		}
		bool preserveDisabled = !desiredTaskEnabled;
		bool exactAgentProcessWasRunning = ScheduledTaskService.IsAgentProcessRunning();
		bool wasRunning = previousTaskXml != null && (taskState == TaskState.Running || exactAgentProcessWasRunning);
		bool oldAgentRunnable = IsTrustedRollbackAgent();
		string stagedAgent = null;
		string stagedControl = null;
		string stagedSwarm = null;
		DeploymentBackup backup = null;
		bool rebuildConfig = false;
		bool rebuildSwarm = false;
		bool tokenProvisioningMarkerOwned = false;
		bool taskMaintenanceMarkerOwned = false;
		bool taskMaintenanceMarkerCanDelete = false;
		string embeddedSwarmSha256 = GetEmbeddedSwarmKeySha256();
		try
		{
			RuntimeSecurityService.EnsureSafeInstallRoot();
			bool migrateLegacySwarmAcl = RuntimeSecurityService.ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm(runAsUser, embeddedSwarmSha256);
			bool flag = inspection.Exists || File.Exists(AppPaths.AgentExe) || File.Exists(AppPaths.ControlExe) || _config.Exists || File.Exists(AppPaths.SwarmKeyFile) || File.Exists(AppPaths.IdentityFile) || File.Exists(AppPaths.ApiTokenFile) || File.Exists(AppPaths.JournalFile) || File.Exists(AppPaths.IdentityProvisioningMarker) || File.Exists(AppPaths.TokenProvisioningMarker) || File.Exists(AppPaths.TaskMaintenanceMarker);
			if (flag && !HasNonEmptyProtectedRuntimeFile(AppPaths.IdentityFile))
			{
				Record("验证设备身份", success: false, "现有部署缺少有效 agent-identity.key；自动修复会更换 PeerID，已在停机前中止。请恢复原 identity 或走明确的重新注册流程。", InstallStepKind.NoMutationFailure);
				return steps;
			}
			bool preserveExistingToken = HasNonEmptyProtectedRuntimeFile(AppPaths.ApiTokenFile);
			bool rotateApiToken = flag && !preserveExistingToken;
			stagedAgent = await StageAndValidateAgentAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			stagedControl = StageControlExecutable();
			JsonObject repairConfig;
			try
			{
				if (!_config.Exists)
				{
					throw new FileNotFoundException();
				}
				repairConfig = _config.Load();
				AgentConfigService.ValidateRuntimeBoundary(repairConfig);
			}
			catch
			{
				repairConfig = AgentConfigService.CreateDefault();
				AgentConfigService.SetAllowedPeers(repairConfig, Array.Empty<string>());
				rebuildConfig = true;
			}
			try
			{
				if (!File.Exists(AppPaths.SwarmKeyFile))
				{
					throw new FileNotFoundException();
				}
				RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
			}
			catch
			{
				stagedSwarm = StageSwarmKey(null);
				RuntimeSecurityService.ValidateSwarmKey(stagedSwarm);
				rebuildSwarm = true;
			}
			Record("验证安装载荷", success: true, "已审查载荷的 SHA-256、AMD64/Console PE、Authenticode 固定值与清单元数据均匹配");
			if (exactAgentProcessWasRunning && previousTaskXml == null)
			{
				throw new InvalidOperationException("检测到无法由受信任计划任务描述的现有 Agent 进程；为避免停机后无法恢复，修复已中止。");
			}
			if (wasRunning && (rebuildConfig || rebuildSwarm))
			{
				throw new InvalidOperationException("现有 Agent 正在运行，但磁盘上的配置或 swarm.key 需要重建，无法保证失败后恢复原健康状态；请先停止 Agent 后再修复。");
			}
			RuntimeIdentitySnapshot expectedRuntime = await CaptureRunningRuntimeIdentityAsync(wasRunning, oldAgentRunnable, ct).ConfigureAwait(continueOnCapturedContext: false);
			EstablishTaskMaintenanceExecutionBoundary(desiredTaskEnabled);
			taskMaintenanceMarkerOwned = !taskMaintenanceMarkerPreexisted;
			backup = await StopAndCaptureBackupAsync(previousTaskXml, previousTaskEnabled, previousFirewallExpected, runAsUser, oldAgentRunnable, !rebuildConfig, !rebuildSwarm, migrateLegacySwarmAcl ? ((Action)delegate
			{
				RuntimeSecurityService.MigrateLegacyEmbeddedSwarmAcl(runAsUser, embeddedSwarmSha256);
			}) : null, wasRunning, expectedRuntime, ct).ConfigureAwait(continueOnCapturedContext: false);
			Record("停止后台进程", success: true, "已验证本产品宿主与 Agent 进程均退出");
			RuntimeSecurityService.PrepareSecureDataRoot(runAsUser);
			Record("校验目录与 ACL", success: true, "停机后完成数据目录加固；运行账户保持为 " + runAsUser);
			AtomicReplace(stagedAgent, AppPaths.AgentExe);
			stagedAgent = null;
			DeployControlExecutable(stagedControl);
			stagedControl = null;
			if (stagedSwarm != null)
			{
				RuntimeSecurityService.ProtectAndValidateRuntimeFile(stagedSwarm, runAsUser);
				AtomicReplace(stagedSwarm, AppPaths.SwarmKeyFile);
				stagedSwarm = null;
				RuntimeSecurityService.ProtectAndValidateRuntimeFile(AppPaths.SwarmKeyFile, runAsUser);
				Record("重建 swarm.key", success: true, "原文件缺失或无效，已使用安装包内置密钥重建");
			}
			if (rebuildConfig)
			{
				_config.Save(repairConfig);
				Record("重建 agent-config.json", success: true, "原配置缺失或无效；已写入安全默认值和空 allowed_peers。未配置获准主控；最终请求策略由 Agent 决定");
			}
			CleanupLegacyLauncher();
			Record("更新程序文件", success: true, AppPaths.AgentExe);
			DeleteIdentityProvisioningMarkerRequired();
			if (rotateApiToken)
			{
				DeleteEmptyProtectedRuntimeFileForProvisioning(AppPaths.ApiTokenFile, "agent-api.token");
				bool flag2 = false;
				try
				{
					flag2 = RuntimeSecurityService.HasValidTokenProvisioningMarker();
				}
				catch
				{
					DeleteTokenProvisioningMarkerRequired();
				}
				EnsureTokenProvisioningMarker();
				tokenProvisioningMarkerOwned = !flag2;
			}
			else
			{
				DeleteTokenProvisioningMarkerRequired();
			}
			_firewall.EnsureExpectedRule(ct);
			ProcessResult processResult = await _task.RegisterAsync(runAsUser, enabled: false, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult.Success)
			{
				throw new InvalidOperationException("重建计划任务失败：" + processResult.CombinedOutput);
			}
			Record("重建开机自启任务", success: true, "保留运行账户：" + runAsUser);
			backup.MarkRuntimeStartAttempted();
			ProcessResult processResult2 = await _task.StartAsync(allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult2.Success)
			{
				throw new InvalidOperationException("启动 Agent 失败：" + processResult2.CombinedOutput);
			}
			if (!(await WaitForReadyAsync(TimeSpan.FromSeconds(45.0), ct).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new TimeoutException("Agent 在 45 秒内未通过本机 API 就绪验证。");
			}
			ValidateGeneratedRuntimeState(runAsUser);
			await backup.ValidatePreservedIdentityAndTokenAsync(ct, preserveIdentity: true, preserveExistingToken).ConfigureAwait(continueOnCapturedContext: false);
			if ((object)expectedRuntime != null)
			{
				await ValidateRuntimeIdentityAsync(expectedRuntime, ct, requireVersionMatch: false).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (rotateApiToken)
			{
				Record("轮换本机 API Token", success: true, "原 agent-api.token 缺失或为空；Agent 已生成新的本机回环 API 凭据，旧凭据不再有效。");
			}
			ProcessResult processResult3 = await _task.SetEnabledAsync(!preserveDisabled, allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult3.Success)
			{
				throw new InvalidOperationException("无法恢复原登录自启偏好：" + processResult3.CombinedOutput);
			}
			DeleteTokenProvisioningMarkerRequired();
			tokenProvisioningMarkerOwned = false;
			RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(runAsUser);
			DeleteTaskMaintenanceMarkerRequired();
			taskMaintenanceMarkerOwned = false;
			string text = null;
			try
			{
				RestoreNormalAgentExecutionAclIfRestricted();
			}
			catch (Exception ex2)
			{
				text = "维护意图已提交，但 Agent 普通执行 ACL 尚未恢复：" + ex2.Message;
			}
			InstallStep installStep = new InstallStep("启动并验证 Agent", Success: true, $"{"127.0.0.1"}:{7432} 已鉴权应答", InstallStepKind.InstallationVerified);
			steps.Add(installStep);
			progress?.Report(installStep);
			if (text != null)
			{
				InstallStep installStep2 = new InstallStep("恢复 Agent 执行权限", Success: false, text, InstallStepKind.CleanupWarning);
				steps.Add(installStep2);
				progress?.Report(installStep2);
			}
			if (!backup.Delete())
			{
				InstallStep installStep3 = new InstallStep("清理回滚备份", Success: false, "无法删除受保护备份：" + backup.RootPath, InstallStepKind.CleanupWarning);
				steps.Add(installStep3);
				progress?.Report(installStep3);
			}
			backup = null;
			return steps;
		}
		catch (Exception ex3)
		{
			InstallStepKind installStepKind = ((ex3 is StopCaptureRecoveredException) ? InstallStepKind.NoMutationFailure : ((!(ex3 is StopCaptureRecoveryFailedException)) ? InstallStepKind.OperationFailure : InstallStepKind.RollbackFailed));
			InstallStepKind kind = installStepKind;
			Record("修复安装", success: false, ex3.Message, kind);
			if (backup != null)
			{
				(bool, string) tuple = await RollbackAsync(backup, previousTaskXml, wasRunning && oldAgentRunnable).ConfigureAwait(continueOnCapturedContext: false);
				if (tuple.Item1 && taskMaintenanceMarkerPreexisted)
				{
					try
					{
						await EstablishTaskMaintenanceMutationBoundaryAsync(runAsUser, CancellationToken.None)
							.ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception boundaryError)
					{
						tuple = (false, tuple.Item2 + "；入口维护意图无法安全恢复为 Mutation：" + boundaryError.Message);
					}
				}
				Record("回滚", tuple.Item1, tuple.Item2, tuple.Item1 ? InstallStepKind.RollbackSucceeded : InstallStepKind.RollbackFailed);
				if (tuple.Item1)
				{
					taskMaintenanceMarkerCanDelete = true;
					bool flag3 = backup.Delete();
					Record("清理回滚备份", flag3, flag3 ? "已删除" : ("无法删除：" + backup.RootPath), (!flag3) ? InstallStepKind.CleanupWarning : InstallStepKind.Normal);
					if (!flag3)
					{
					}
				}
			}
			if (ex3 is StopCaptureRecoveredException)
			{
				if (taskMaintenanceMarkerPreexisted)
				{
					try
					{
						await EstablishTaskMaintenanceMutationBoundaryAsync(runAsUser, CancellationToken.None)
							.ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception boundaryError)
					{
						Record("恢复维护状态", success: false,
							"入口维护意图无法安全恢复为 Mutation：" + boundaryError.Message,
							InstallStepKind.RollbackFailed);
					}
				}
				else
				{
					taskMaintenanceMarkerCanDelete = true;
				}
			}
			return steps;
		}
		finally
		{
			if (taskMaintenanceMarkerOwned && taskMaintenanceMarkerCanDelete)
			{
				TryDeleteTaskMaintenanceMarker();
				if (!File.Exists(AppPaths.TaskMaintenanceMarker))
				{
					try
					{
						RestoreNormalAgentExecutionAclIfRestricted();
					}
					catch
					{
					}
				}
			}
			if (tokenProvisioningMarkerOwned && taskMaintenanceMarkerCanDelete)
			{
				TryDeleteTokenProvisioningMarker();
			}
			TryDelete(stagedAgent);
			TryDelete(stagedControl);
			TryDelete(stagedSwarm);
		}
		InstallStep Record(string title, bool success, string detail, InstallStepKind kind2 = InstallStepKind.Normal)
		{
			InstallStep installStep4 = new InstallStep(title, success, detail, kind2);
			steps.Add(installStep4);
			progress?.Report(installStep4);
			return installStep4;
		}
	}

	public async Task<IReadOnlyList<InstallStep>> InstallAsync(InstallOptions options, IProgress<InstallStep>? progress = null, CancellationToken ct = default(CancellationToken))
	{
		List<InstallStep> steps = new List<InstallStep>();
		string stagedAgent = null;
		string stagedControl = null;
		string stagedSwarm = null;
		DeploymentBackup backup = null;
		bool mutationStarted = false;
		bool provisioningMarkerOwned = false;
		bool tokenProvisioningMarkerOwned = false;
		bool taskMaintenanceMarkerOwned = false;
		bool taskMaintenanceMarkerCanDelete = false;
		bool tokenMarkerInvalid = false;
		if (HasInterruptedUninstallArtifacts)
		{
			InstallStep installStep = new InstallStep("检查未完成卸载", Success: false, "检测到未处理的卸载恢复状态；安装不会覆盖该状态。请重新启动管理器并选择继续或回滚卸载。", InstallStepKind.NoMutationFailure);
			steps.Add(installStep);
			progress?.Report(installStep);
			return steps;
		}
		if (!TryGetPendingControlSelfDelete(out bool pending, out string error))
		{
			InstallStep installStep2 = new InstallStep("检查重启后清理状态", Success: false, "无法确认 Windows 待处理文件删除队列，安装已安全中止：" + error, InstallStepKind.NoMutationFailure);
			steps.Add(installStep2);
			progress?.Report(installStep2);
			return steps;
		}
		if (pending)
		{
			InstallStep installStep3 = new InstallStep("检查重启后清理状态", Success: false, "上一次卸载安排的控制程序删除仍待 Windows 重启执行。请先重新启动 Windows，再重新安装。", InstallStepKind.NoMutationFailure);
			steps.Add(installStep3);
			progress?.Report(installStep3);
			return steps;
		}
		ScheduledTaskInspection previousInspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (previousInspection.QueryFailed)
		{
			InstallStep installStep4 = new InstallStep("读取现有计划任务", Success: false, "无法确认现有任务状态，安装已安全中止：" + previousInspection.QueryError, InstallStepKind.NoMutationFailure);
			steps.Add(installStep4);
			progress?.Report(installStep4);
			return steps;
		}
		FirewallRuleInspection previousFirewallInspection = _firewall.Inspect();
		if (previousFirewallInspection.QueryFailed)
		{
			InstallStep installStep5 = new InstallStep("读取现有 Windows Firewall 规则", Success: false, "无法确认产品防火墙规则状态，安装已安全中止：" + previousFirewallInspection.QueryError, InstallStepKind.NoMutationFailure);
			steps.Add(installStep5);
			progress?.Report(installStep5);
			return steps;
		}
		bool previousFirewallExpected = previousFirewallInspection.Exists && previousFirewallInspection.MatchesExpectedDefinition;
		string previousTaskXml = ((previousInspection.Exists && previousInspection.MatchesExpectedDefinition) ? previousInspection.RawXml : null);
		TaskState taskState = await _task.GetStateAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		bool previousTaskEnabled = previousTaskXml != null && previousInspection.EffectiveEnabled;
		bool desiredTaskEnabled = previousTaskXml == null || previousTaskEnabled;
		bool taskMaintenanceMarkerPreexisted;
		try
		{
			taskMaintenanceMarkerPreexisted = RuntimeSecurityService.TryReadTaskMaintenanceMarker(out var desiredEnabled);
			if (taskMaintenanceMarkerPreexisted)
			{
				desiredTaskEnabled = desiredEnabled;
			}
		}
		catch (Exception ex)
		{
			InstallStep installStep6 = new InstallStep("读取维护状态", Success: false, "计划任务维护意图无法安全核验，安装已中止：" + ex.Message, InstallStepKind.NoMutationFailure);
			steps.Add(installStep6);
			progress?.Report(installStep6);
			return steps;
		}
		bool preserveDisabled = !desiredTaskEnabled;
		bool exactAgentProcessWasRunning = ScheduledTaskService.IsAgentProcessRunning();
		bool wasRunning = previousTaskXml != null && (taskState == TaskState.Running || exactAgentProcessWasRunning);
		bool oldAgentRunnable = IsTrustedRollbackAgent();
		try
		{
			ValidateInstallOptions(options);
			bool flag = previousInspection.Exists || previousFirewallInspection.Exists || File.Exists(AppPaths.AgentExe) || File.Exists(AppPaths.ControlExe) || File.Exists(AppPaths.ConfigFile) || File.Exists(AppPaths.SwarmKeyFile) || File.Exists(AppPaths.IdentityFile) || File.Exists(AppPaths.ApiTokenFile) || File.Exists(AppPaths.JournalFile) || File.Exists(AppPaths.IdentityProvisioningMarker) || File.Exists(AppPaths.TokenProvisioningMarker);
			bool flag2 = HasNonEmptyProtectedRuntimeFile(AppPaths.IdentityFile);
			bool identityMarkerPreexisted = RuntimeSecurityService.HasValidIdentityProvisioningMarker();
			bool flag3 = HasNonEmptyProtectedRuntimeFile(AppPaths.ApiTokenFile);
			bool tokenMarkerPreexisted = false;
			try
			{
				tokenMarkerPreexisted = RuntimeSecurityService.HasValidTokenProvisioningMarker();
			}
			catch
			{
				tokenMarkerInvalid = File.Exists(AppPaths.TokenProvisioningMarker);
			}
			if (flag && !flag2 && !identityMarkerPreexisted)
			{
				throw new InvalidOperationException("检测到残缺的既有部署，但 agent-identity.key 缺失或为空；安装不会静默生成新 PeerID。请恢复身份备份，或先按明确的新设备流程清理旧部署后再安装。");
			}
			bool provisionNewIdentity = !flag2;
			bool provisionNewToken = !flag3;
			RuntimeSecurityService.EnsureSafeInstallRoot();
			string embeddedSwarmSha256 = GetEmbeddedSwarmKeySha256();
			bool migrateLegacySwarmAcl = RuntimeSecurityService.ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm(options.RunAsUser, embeddedSwarmSha256);
			stagedAgent = await StageAndValidateAgentAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			stagedControl = StageControlExecutable();
			bool restoreExistingConfig = false;
			try
			{
				if (_config.Exists)
				{
					AgentConfigService.ValidateRuntimeBoundary(_config.Load());
					restoreExistingConfig = true;
				}
			}
			catch
			{
			}
			bool keepExistingSwarm = false;
			if (File.Exists(AppPaths.SwarmKeyFile))
			{
				try
				{
					RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
					keepExistingSwarm = true;
				}
				catch
				{
				}
			}
			if (!keepExistingSwarm)
			{
				stagedSwarm = StageSwarmKey(options.SwarmKeySourcePath);
				RuntimeSecurityService.ValidateSwarmKey(stagedSwarm);
			}
			Record("验证安装载荷", success: true, "Agent 完整性及 swarm.key 格式验证通过");
			if (exactAgentProcessWasRunning && previousTaskXml == null)
			{
				throw new InvalidOperationException("检测到无法由受信任计划任务描述的现有 Agent 进程；为避免停机后无法恢复，安装已中止。");
			}
			if (wasRunning && (!restoreExistingConfig || !keepExistingSwarm))
			{
				throw new InvalidOperationException("现有 Agent 正在运行，但磁盘上的配置或 swarm.key 不能作为可信回滚输入；为避免停机后无法恢复健康状态，安装已中止。");
			}
			RuntimeIdentitySnapshot expectedRuntime = await CaptureRunningRuntimeIdentityAsync(wasRunning, oldAgentRunnable, ct).ConfigureAwait(continueOnCapturedContext: false);
			mutationStarted = true;
			EstablishTaskMaintenanceExecutionBoundary(desiredTaskEnabled);
			taskMaintenanceMarkerOwned = !taskMaintenanceMarkerPreexisted;
			backup = await StopAndCaptureBackupAsync(previousTaskXml, previousTaskEnabled, previousFirewallExpected, options.RunAsUser, oldAgentRunnable, restoreExistingConfig, keepExistingSwarm, migrateLegacySwarmAcl ? ((Action)delegate
			{
				RuntimeSecurityService.MigrateLegacyEmbeddedSwarmAcl(options.RunAsUser, embeddedSwarmSha256);
			}) : null, wasRunning, expectedRuntime, ct).ConfigureAwait(continueOnCapturedContext: false);
			Record("停止已有 Agent 实例", success: true, "仅停止安装路径精确匹配的进程");
			if (provisionNewIdentity)
			{
				EnsureIdentityProvisioningMarker();
				provisioningMarkerOwned = !identityMarkerPreexisted;
			}
			RuntimeSecurityService.PrepareSecureDataRoot(options.RunAsUser);
			Directory.CreateDirectory(AppPaths.LogDirectory);
			Record("创建并保护安装目录", success: true, options.HardenAcl ? "停机后已将数据目录完整 DACL 替换为运行账户、Administrators 与 SYSTEM" : "安全边界要求强制启用完整 DACL；未采用跳过 ACL 的请求");
			AtomicReplace(stagedAgent, AppPaths.AgentExe);
			stagedAgent = null;
			DeployControlExecutable(stagedControl);
			stagedControl = null;
			if (stagedSwarm != null)
			{
				RuntimeSecurityService.ProtectAndValidateRuntimeFile(stagedSwarm, options.RunAsUser);
				AtomicReplace(stagedSwarm, AppPaths.SwarmKeyFile);
				stagedSwarm = null;
				RuntimeSecurityService.ProtectAndValidateRuntimeFile(AppPaths.SwarmKeyFile, options.RunAsUser);
				Record("写入 swarm.key", success: true, HasEmbeddedSwarmKey ? "来源：安装包内置" : "来源：用户选择的文件");
			}
			else
			{
				Record("写入 swarm.key", success: true, "保留并验证本机已有的 swarm.key");
			}
			CleanupLegacyLauncher();
			Record("部署程序文件", success: true, AppPaths.AgentExe);
			JsonObject jsonObject;
			try
			{
				jsonObject = (_config.Exists ? _config.Load() : AgentConfigService.CreateDefault());
			}
			catch
			{
				jsonObject = AgentConfigService.CreateDefault();
			}
			jsonObject["agent_name"] = options.AgentName;
			AgentConfigService.SetStringArray(jsonObject, "agent_tags", options.AgentTags);
			AgentConfigService.SetStringArray(jsonObject, "bootstrap_addrs", options.BootstrapAddrs);
			AgentConfigService.SetAllowedPeers(jsonObject, options.AllowedPeers);
			jsonObject["swarm_key"] = AgentConfigService.ToJsonPath(AppPaths.SwarmKeyFile);
			jsonObject["identity_file"] = AgentConfigService.ToJsonPath(AppPaths.IdentityFile);
			jsonObject["api_token_file"] = AgentConfigService.ToJsonPath(AppPaths.ApiTokenFile);
			jsonObject["command_journal_file"] = AgentConfigService.ToJsonPath(AppPaths.JournalFile);
			jsonObject["api_listen"] = $"{"127.0.0.1"}:{7432}";
			jsonObject["rendezvous_group"] = options.RendezvousGroup;
			jsonObject["max_parallel_tasks"] = options.MaxParallelTasks;
			jsonObject["max_transfer_bytes"] = options.MaxTransferBytes;
			_config.Save(jsonObject);
			Record("写入 agent-config.json", success: true, (options.AllowedPeers.Count == 0) ? "已写入空 allowed_peers，未配置获准主控；最终请求策略由 Agent 决定" : $"已授权 {options.AllowedPeers.Count} 个主控 PeerID");
			if (tokenMarkerInvalid)
			{
				DeleteTokenProvisioningMarkerRequired();
			}
			if (provisionNewIdentity)
			{
				DeleteEmptyProtectedRuntimeFileForProvisioning(AppPaths.IdentityFile, "agent-identity.key");
				if (provisionNewToken)
				{
					DeleteEmptyProtectedRuntimeFileForProvisioning(AppPaths.ApiTokenFile, "agent-api.token");
				}
				DeleteTokenProvisioningMarkerRequired();
			}
			else
			{
				DeleteIdentityProvisioningMarkerRequired();
				if (provisionNewToken)
				{
					DeleteEmptyProtectedRuntimeFileForProvisioning(AppPaths.ApiTokenFile, "agent-api.token");
					EnsureTokenProvisioningMarker();
					tokenProvisioningMarkerOwned = !tokenMarkerPreexisted;
				}
				else
				{
					DeleteTokenProvisioningMarkerRequired();
				}
			}
			_firewall.EnsureExpectedRule(ct);
			ProcessResult processResult = await _task.RegisterAsync(options.RunAsUser, enabled: false, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult.Success)
			{
				throw new InvalidOperationException("注册计划任务失败：" + processResult.CombinedOutput);
			}
			Record("注册开机自启任务", success: true, "运行账户：" + options.RunAsUser);
			backup.MarkRuntimeStartAttempted();
			ProcessResult processResult2 = await _task.StartAsync(allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult2.Success)
			{
				throw new InvalidOperationException("启动 Agent 失败：" + processResult2.CombinedOutput);
			}
			if (!(await WaitForReadyAsync(TimeSpan.FromSeconds(45.0), ct).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new TimeoutException("Agent 在 45 秒内未通过本机 API 就绪验证。");
			}
			ValidateGeneratedRuntimeState(options.RunAsUser);
			await backup.ValidatePreservedIdentityAndTokenAsync(ct, !provisionNewIdentity, !provisionNewToken).ConfigureAwait(continueOnCapturedContext: false);
			if ((object)expectedRuntime != null)
			{
				await ValidateRuntimeIdentityAsync(expectedRuntime, ct, requireVersionMatch: false).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (provisionNewToken && !provisionNewIdentity)
			{
				Record("轮换本机 API Token", success: true, "原 agent-api.token 缺失或为空；Agent 已生成新的本机回环 API 凭据，旧凭据不再有效。");
			}
			ProcessResult processResult3 = await _task.SetEnabledAsync(!preserveDisabled, allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult3.Success)
			{
				throw new InvalidOperationException("无法恢复原登录自启偏好：" + processResult3.CombinedOutput);
			}
			DeleteIdentityProvisioningMarkerRequired();
			DeleteTokenProvisioningMarkerRequired();
			provisioningMarkerOwned = false;
			tokenProvisioningMarkerOwned = false;
			RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(options.RunAsUser);
			DeleteTaskMaintenanceMarkerRequired();
			taskMaintenanceMarkerOwned = false;
			string text = null;
			try
			{
				RestoreNormalAgentExecutionAclIfRestricted();
			}
			catch (Exception ex2)
			{
				text = "维护意图已提交，但 Agent 普通执行 ACL 尚未恢复：" + ex2.Message;
			}
			InstallStep installStep7 = new InstallStep("启动并验证 Agent", Success: true, $"{"127.0.0.1"}:{7432} 已鉴权应答", InstallStepKind.InstallationVerified);
			steps.Add(installStep7);
			progress?.Report(installStep7);
			if (text != null)
			{
				InstallStep installStep8 = new InstallStep("恢复 Agent 执行权限", Success: false, text, InstallStepKind.CleanupWarning);
				steps.Add(installStep8);
				progress?.Report(installStep8);
			}
			if (!backup.Delete())
			{
				InstallStep installStep9 = new InstallStep("清理回滚备份", Success: false, "无法删除受保护备份：" + backup.RootPath, InstallStepKind.CleanupWarning);
				steps.Add(installStep9);
				progress?.Report(installStep9);
			}
			backup = null;
			return steps;
		}
		catch (Exception ex3)
		{
			InstallStepKind installStepKind = ((ex3 is StopCaptureRecoveredException) ? InstallStepKind.NoMutationFailure : ((!(ex3 is StopCaptureRecoveryFailedException)) ? (mutationStarted ? InstallStepKind.OperationFailure : InstallStepKind.NoMutationFailure) : InstallStepKind.RollbackFailed));
			InstallStepKind kind = installStepKind;
			InstallStep installStep10 = new InstallStep("安装中断", Success: false, ex3.Message, kind);
			steps.Add(installStep10);
			progress?.Report(installStep10);
			if (backup != null)
			{
				(bool, string) tuple = await RollbackAsync(backup, previousTaskXml, wasRunning && oldAgentRunnable).ConfigureAwait(continueOnCapturedContext: false);
				if (tuple.Item1 && taskMaintenanceMarkerPreexisted)
				{
					try
					{
						await EstablishTaskMaintenanceMutationBoundaryAsync(options.RunAsUser, CancellationToken.None)
							.ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception boundaryError)
					{
						tuple = (false, tuple.Item2 + "；入口维护意图无法安全恢复为 Mutation：" + boundaryError.Message);
					}
				}
				InstallStep installStep11 = new InstallStep("回滚", tuple.Item1, tuple.Item2, tuple.Item1 ? InstallStepKind.RollbackSucceeded : InstallStepKind.RollbackFailed);
				steps.Add(installStep11);
				progress?.Report(installStep11);
				if (tuple.Item1)
				{
					taskMaintenanceMarkerCanDelete = true;
					bool flag4 = backup.Delete();
					InstallStep installStep12 = new InstallStep("清理回滚备份", flag4, flag4 ? "已删除" : ("无法删除：" + backup.RootPath), (!flag4) ? InstallStepKind.CleanupWarning : InstallStepKind.Normal);
					steps.Add(installStep12);
					progress?.Report(installStep12);
					if (!flag4)
					{
					}
				}
			}
			if (ex3 is StopCaptureRecoveredException)
			{
				if (taskMaintenanceMarkerPreexisted)
				{
					try
					{
						await EstablishTaskMaintenanceMutationBoundaryAsync(options.RunAsUser, CancellationToken.None)
							.ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception boundaryError)
					{
						InstallStep failedBoundary = new InstallStep("恢复维护状态", Success: false,
							"入口维护意图无法安全恢复为 Mutation：" + boundaryError.Message,
							InstallStepKind.RollbackFailed);
						steps.Add(failedBoundary);
						progress?.Report(failedBoundary);
					}
				}
				else
				{
					taskMaintenanceMarkerCanDelete = true;
				}
			}
			return steps;
		}
		finally
		{
			if (taskMaintenanceMarkerOwned && taskMaintenanceMarkerCanDelete)
			{
				TryDeleteTaskMaintenanceMarker();
				if (!File.Exists(AppPaths.TaskMaintenanceMarker))
				{
					try
					{
						RestoreNormalAgentExecutionAclIfRestricted();
					}
					catch
					{
					}
				}
			}
			if (provisioningMarkerOwned && taskMaintenanceMarkerCanDelete)
			{
				TryDeleteIdentityProvisioningMarker();
			}
			if (tokenProvisioningMarkerOwned && taskMaintenanceMarkerCanDelete)
			{
				TryDeleteTokenProvisioningMarker();
			}
			TryDelete(stagedAgent);
			TryDelete(stagedControl);
			TryDelete(stagedSwarm);
		}
		InstallStep Record(string title, bool success, string detail)
		{
			InstallStep installStep13 = new InstallStep(title, success, detail);
			steps.Add(installStep13);
			progress?.Report(installStep13);
			return installStep13;
		}
	}

	public static Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default(CancellationToken))
	{
		return WaitForReadyAsync(timeout, RuntimeSecurityService.ExpectedAgentVersion, ct);
	}

	private static async Task<bool> WaitForReadyAsync(TimeSpan timeout, string expectedVersion, CancellationToken ct)
	{
		DateTime deadline = DateTime.UtcNow + timeout;
		using ControlApiClient client = new ControlApiClient();
		while (DateTime.UtcNow < deadline)
		{
			ct.ThrowIfCancellationRequested();
			bool flag = ScheduledTaskService.IsAgentProcessRunning();
			if (flag)
			{
				flag = await ControlApiClient.IsPortOpenAsync(500, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			if (flag)
			{
				AgentInfo agentInfo = await client.GetInfoAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if ((object)agentInfo != null && string.Equals(agentInfo.Version, expectedVersion, StringComparison.Ordinal))
				{
					return true;
				}
			}
			await Task.Delay(500, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		return false;
	}

	public static async Task RestartVerifiedAsync(TimeSpan timeout, CancellationToken ct = default(CancellationToken))
	{
		ScheduledTaskService task = new ScheduledTaskService();
		ProcessResult processResult = await task.StopAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!processResult.Success)
		{
			throw new InvalidOperationException("无法确认 Agent 已停止：" + processResult.CombinedOutput);
		}
		ProcessResult processResult2 = await task.StartAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!processResult2.Success)
		{
			throw new InvalidOperationException("无法确认 Agent 已启动：" + processResult2.CombinedOutput);
		}
		if (!(await WaitForReadyAsync(timeout, ct).ConfigureAwait(continueOnCapturedContext: false)))
		{
			throw new TimeoutException($"Agent 未在 {timeout.TotalSeconds:0} 秒内通过鉴权就绪验证。");
		}
	}

	public static InterruptedUninstallRecovery GetInterruptedUninstallRecovery()
	{
		UninstallRecoveryState uninstallRecoveryState = ReadUninstallRecoveryState();
		bool removeData = uninstallRecoveryState.RemoveData;
		bool canContinue = !string.Equals(uninstallRecoveryState.Phase, "prepared", StringComparison.Ordinal);
		string phase = uninstallRecoveryState.Phase;
		bool canRollback = ((phase == "prepared" || phase == "snapshot-ready") ? true : false);
		return new InterruptedUninstallRecovery(removeData, canContinue, canRollback, uninstallRecoveryState.Phase);
	}

	public async Task<IReadOnlyList<InstallStep>> ResumeInterruptedUninstallAsync(CancellationToken ct = default(CancellationToken))
	{
		List<InstallStep> steps = new List<InstallStep>();
		UninstallRecoveryState state = ReadUninstallRecoveryState();
		if (string.Equals(state.Phase, "prepared", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("卸载只记录了准备意图，尚无完整停机快照；只能回滚该准备状态，不能直接提交删除。");
		}
		await EstablishTaskMaintenanceMutationBoundaryAsync(state.RuntimeUser, ct)
			.ConfigureAwait(continueOnCapturedContext: false);
		if (string.Equals(state.Phase, "snapshot-ready", StringComparison.Ordinal))
		{
			state = state with
			{
				Phase = "commit-started"
			};
			WriteUninstallRecoveryState(state);
		}
		if (state.RemoveData)
		{
			QuarantineDataRootAfterCommit(state);
			steps.Add(new InstallStep("隔离运行数据", Success: true, state.DataRootWasPresent ? AppPaths.UninstallRecoveryDataRoot : "原本没有运行数据"));
		}
		ScheduledTaskInspection inspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (inspection.QueryFailed)
		{
			throw new InvalidOperationException("无法确认待完成卸载的计划任务状态：" + inspection.QueryError);
		}
		if (inspection.Exists && !inspection.MatchesExpectedDefinition)
		{
			throw new InvalidOperationException("同名计划任务不再匹配产品精确定义，拒绝在恢复流程中删除：" + string.Join("；", inspection.Issues));
		}
		if (inspection.Exists)
		{
			ProcessResult processResult3 = await _task.DeleteAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult3.Success)
			{
				throw new InvalidOperationException("无法删除计划任务：" + processResult3.CombinedOutput);
			}
			ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (scheduledTaskInspection.QueryFailed || scheduledTaskInspection.Exists)
			{
				throw new InvalidOperationException("计划任务删除后复核失败：" + scheduledTaskInspection.QueryError);
			}
		}
		steps.Add(new InstallStep("隔离计划任务", Success: true, inspection.Exists ? "已删除并复核不存在" : "任务已不存在"));
		FirewallRuleInspection firewallRuleInspection = _firewall.Inspect();
		if (firewallRuleInspection.QueryFailed)
		{
			throw new InvalidOperationException("无法确认 Windows Firewall 产品规则状态：" + firewallRuleInspection.QueryError);
		}
		if (firewallRuleInspection.Exists && !firewallRuleInspection.MatchesExpectedDefinition)
		{
			throw new InvalidOperationException("同名 Windows Firewall 规则不再匹配产品精确定义，拒绝删除：" + string.Join("；", firewallRuleInspection.Issues));
		}
		if (firewallRuleInspection.Exists)
		{
			_firewall.DeleteProductRule(ct);
		}
		DeleteIdentityProvisioningMarkerRequired();
		DeleteTokenProvisioningMarkerRequired();
		TryDeleteRequired(AppPaths.AgentExe);
		CleanupLegacyLauncher();
		if (state.RemoveData)
		{
			if (Directory.Exists("C:\\ProgramData\\P2PAgent") || File.Exists("C:\\ProgramData\\P2PAgent"))
			{
				throw new IOException("删除数据卸载已提交，但 DataRoot 再次出现；拒绝盲目删除该路径。");
			}
			if (Directory.Exists(AppPaths.UninstallRecoveryDataRoot))
			{
				RuntimeSecurityService.ProtectMovedDataRootForQuarantine(AppPaths.UninstallRecoveryDataRoot, state.RuntimeUser);
				RuntimeSecurityService.DeleteProtectedRollbackTree(AppPaths.UninstallRecoveryDataRoot);
			}
			else if (File.Exists(AppPaths.UninstallRecoveryDataRoot))
			{
				throw new IOException("卸载数据隔离路径被文件占用，拒绝删除。");
			}
			steps.Add(new InstallStep("清理隔离数据", Success: true, state.DataRootWasPresent ? "已删除设备身份、配置与任务记录" : "原本没有运行数据"));
		}
		else
		{
			if (state.DataRootWasPresent)
			{
				if (!Directory.Exists("C:\\ProgramData\\P2PAgent"))
				{
					throw new IOException("保留数据卸载已提交，但原 DataRoot 缺失；拒绝把数据保留误报为成功。");
				}
				RuntimeSecurityService.ValidateExistingDataRootTrust(state.RuntimeUser);
			}
			if (Directory.Exists(AppPaths.UninstallRecoveryDataRoot) || File.Exists(AppPaths.UninstallRecoveryDataRoot))
			{
				throw new IOException("保留数据卸载出现了不应存在的隔离数据路径。");
			}
			steps.Add(new InstallStep("保留运行数据", Success: true, state.DataRootWasPresent ? "设备身份与配置保留在 C:\\ProgramData\\P2PAgent" : "原本没有运行数据"));
		}
		string processPath = Environment.ProcessPath;
		bool flag = processPath != null && PathsEqual(processPath, AppPaths.ControlExe);
		if (!flag)
		{
			TryDeleteRequired(AppPaths.ControlExe);
		}
		else
		{
			if (!TryGetPendingControlSelfDelete(out bool pending, out string error))
			{
				throw new InvalidOperationException("无法读取控制程序延迟删除状态：" + error);
			}
			if (!pending)
			{
				steps.Add(new InstallStep("安排重启后清理", Success: true, ScheduleSelfDelete(), InstallStepKind.DeferredCleanup));
			}
		}
		DeleteTaskMaintenanceMarkerRequired();
		FinalizeUninstallRecovery();
		steps.Add(new InstallStep("清理回滚点", Success: true, "卸载恢复状态与停机快照已删除"));
		steps.Add(new InstallStep("移除程序文件", Success: true, flag ? "Agent 已移除；控制程序等待 Windows 重启清理" : "程序文件已移除"));
		TryDeleteEmptyDirectory("C:\\Program Files\\P2PAgent");
		return steps;
	}

	public async Task<IReadOnlyList<InstallStep>> RollbackInterruptedUninstallAsync(CancellationToken ct = default(CancellationToken))
	{
		UninstallRecoveryState state = ReadUninstallRecoveryState();
		string phase = state.Phase;
		bool desiredEnabled = ((phase == "prepared" || phase == "snapshot-ready") ? true : false);
		if (!desiredEnabled)
		{
			throw new InvalidOperationException("卸载已进入前向提交阶段，只能继续完成，不能再自动回滚。");
		}
		await EstablishTaskMaintenanceMutationBoundaryAsync(state.RuntimeUser, ct)
			.ConfigureAwait(continueOnCapturedContext: false);
		if (state.TaskWasPresent && !state.TaskMaintenanceMarkerPreexisted)
		{
			if (!RuntimeSecurityService.TryReadTaskMaintenanceMarker(out bool recoveredDesiredEnabled,
				out RuntimeSecurityService.TaskMaintenancePhase recoveredPhase))
			{
				// The durable uninstall recovery root is published before a newly-owned
				// task-maintenance marker. Recreate that marker from the captured entry
				// task state when power loss lands in that narrow publication window.
				EnsureTaskMaintenanceMarker(state.TaskWasEnabled);
			}
			else if (recoveredDesiredEnabled != state.TaskWasEnabled ||
				recoveredPhase != RuntimeSecurityService.TaskMaintenancePhase.Mutation)
			{
				throw new InvalidDataException("卸载回滚维护意图与入口实际任务状态不一致。 ");
			}
		}
		ValidateRollbackDataRootUnmoved(state);
		FirewallRuleInspection firewallRuleInspection = _firewall.Inspect();
		if (firewallRuleInspection.QueryFailed || firewallRuleInspection.Exists != state.FirewallRuleWasPresent || (firewallRuleInspection.Exists && !firewallRuleInspection.MatchesExpectedDefinition))
		{
			throw new InvalidOperationException("提交前 Windows Firewall 规则已发生变化，拒绝把恢复误报为成功：" + string.Join("；", from value in firewallRuleInspection.Issues.Append(firewallRuleInspection.QueryError)
				where !string.IsNullOrWhiteSpace(value)
				select value));
		}
		ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (scheduledTaskInspection.QueryFailed || scheduledTaskInspection.Exists != state.TaskWasPresent || (scheduledTaskInspection.Exists && !scheduledTaskInspection.MatchesExpectedDefinition))
		{
			throw new InvalidOperationException("提交前计划任务已发生变化，拒绝自动恢复：" + string.Join("；", from value in scheduledTaskInspection.Issues.Append(scheduledTaskInspection.QueryError)
				where !string.IsNullOrWhiteSpace(value)
				select value));
		}
		RuntimeIdentitySnapshot expectedRuntime = ExpectedRuntimeFromState(state);
		(bool, string) tuple = await RestoreAfterCaptureFailureAsync(state.TaskWasPresent ? scheduledTaskInspection.RawXml : null, state.TaskWasEnabled, state.RuntimeUser, state.WasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
		if (!tuple.Item1)
		{
			throw new InvalidOperationException("卸载恢复未通过任务/运行态核验：" + tuple.Item2);
		}
		if (!state.TaskMaintenanceMarkerPreexisted)
		{
			DeleteTaskMaintenanceMarkerRequired();
		}
		else
		{
			if (!RuntimeSecurityService.TryReadTaskMaintenanceMarker(out desiredEnabled))
			{
				throw new InvalidDataException("入口时已有的计划任务维护意图在恢复期间丢失。");
			}
			await EstablishTaskMaintenanceMutationBoundaryAsync(state.RuntimeUser, ct)
				.ConfigureAwait(continueOnCapturedContext: false);
		}
		FinalizeUninstallRecovery();
		if (!state.TaskMaintenanceMarkerPreexisted)
		{
			if (RuntimeSecurityService.HasMaintenanceArtifacts)
			{
				throw new InvalidDataException("卸载回滚完成后仍存在活动维护工件，拒绝恢复普通 Agent 执行权限。 ");
			}
			RestoreNormalAgentExecutionAclIfRestricted();
		}
		return new InstallStep[1]
		{
			new InstallStep("卸载回滚", Success: true, "已恢复隔离数据、入口实际任务 Enabled 值及原运行状态，并删除受保护恢复状态")
		};
	}

	private static UninstallRecoveryState CreateUninstallRecoveryState(bool removeData, bool dataRootWasPresent, string runtimeUser, bool taskWasPresent, bool taskWasEnabled, bool wasRunning, bool firewallRuleWasPresent, bool taskMaintenanceMarkerPreexisted, RuntimeIdentitySnapshot? expectedRuntime)
	{
		return new UninstallRecoveryState(1, "prepared", removeData, dataRootWasPresent, runtimeUser, taskWasPresent, taskWasEnabled, wasRunning, firewallRuleWasPresent, taskMaintenanceMarkerPreexisted, expectedRuntime?.PeerId, expectedRuntime?.AgentVersion, expectedRuntime?.IdentitySha256, expectedRuntime?.TokenSha256);
	}

	private static void InitializeUninstallRecovery(UninstallRecoveryState state)
	{
		RuntimeSecurityService.EnsureSafeInstallRoot();
		CleanupInterruptedUninstallTombstones();
		if (Directory.Exists(AppPaths.UninstallRecoveryRoot) || File.Exists(AppPaths.UninstallRecoveryRoot))
		{
			throw new InvalidOperationException("检测到尚未处理的卸载恢复状态；请先继续或回滚上一次卸载。");
		}
		RuntimeSecurityService.PrepareSecureRollbackDirectory(AppPaths.UninstallRecoveryStageRoot);
		WriteUninstallRecoveryStateToRoot(state, AppPaths.UninstallRecoveryStageRoot);
		if (!MoveFileEx(AppPaths.UninstallRecoveryStageRoot, AppPaths.UninstallRecoveryRoot, 8u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久发布卸载恢复目录。");
		}
		if (ReadUninstallRecoveryState() != state)
		{
			throw new IOException("卸载恢复目录发布后读回不一致。");
		}
	}

	private static void InitializeUninstallRecoveryWithExecutionBoundary(UninstallRecoveryState state)
	{
		RuntimeSecurityService.RestrictAgentExecutionForMaintenance(AppPaths.AgentExe);
		try
		{
			RuntimeSecurityService.RestoreTaskMaintenanceMutationPhaseIfPresent();
			InitializeUninstallRecovery(state);
		}
		catch (Exception innerException)
		{
			if (!RuntimeSecurityService.HasMaintenanceArtifacts)
			{
				try
				{
					RestoreNormalAgentExecutionAclIfRestricted();
				}
				catch (Exception ex)
				{
					throw new IOException("卸载恢复意图发布失败，且 Agent 普通执行 ACL 无法恢复：" + ex.Message, innerException);
				}
			}
			throw;
		}
	}

	private static void WriteUninstallRecoveryState(UninstallRecoveryState state)
	{
		WriteUninstallRecoveryStateToRoot(state, AppPaths.UninstallRecoveryRoot);
	}

	private static void WriteUninstallRecoveryStateToRoot(UninstallRecoveryState state, string recoveryRoot)
	{
		ValidateUninstallRecoveryState(state);
		RuntimeSecurityService.ValidateProtectedRollbackDirectory(recoveryRoot);
		string text = Path.Combine(recoveryRoot, "state.json");
		byte[] array = JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions
		{
			WriteIndented = true
		});
		int num = array.Length;
		if ((num <= 0 || num > 32768) ? true : false)
		{
			throw new InvalidDataException("卸载恢复状态大小超出边界。");
		}
		string text2 = Path.Combine(recoveryRoot, $".state-{Guid.NewGuid():N}.stage");
		try
		{
			using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				fileStream.Write(array);
				fileStream.Flush(flushToDisk: true);
			}
			RuntimeSecurityService.ProtectRollbackFile(recoveryRoot, text2);
			bool flag = File.Exists(text);
			if (Directory.Exists(text))
			{
				throw new IOException("卸载恢复状态路径被目录占用。");
			}
			if (flag)
			{
				RuntimeSecurityService.ReadProtectedRollbackTextFile(recoveryRoot, text, 32768);
			}
			uint flags = 8u | (flag ? 1u : 0u);
			if (!MoveFileEx(text2, text, flags))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久发布卸载恢复状态。");
			}
			RuntimeSecurityService.ProtectRollbackFile(recoveryRoot, text);
			if (ReadUninstallRecoveryStateFromRoot(recoveryRoot) != state)
			{
				throw new IOException("卸载恢复状态写入后读回不一致。");
			}
		}
		finally
		{
			TryDelete(text2);
		}
	}

	private static UninstallRecoveryState ReadUninstallRecoveryState()
	{
		return ReadUninstallRecoveryStateFromRoot(AppPaths.UninstallRecoveryRoot);
	}

	private static UninstallRecoveryState ReadUninstallRecoveryStateFromRoot(string recoveryRoot)
	{
		string path = Path.Combine(recoveryRoot, "state.json");
		UninstallRecoveryState uninstallRecoveryState = JsonSerializer.Deserialize<UninstallRecoveryState>(RuntimeSecurityService.ReadProtectedRollbackTextFile(recoveryRoot, path, 32768)) ?? throw new InvalidDataException("卸载恢复状态为空。");
		ValidateUninstallRecoveryState(uninstallRecoveryState);
		if (string.Equals(recoveryRoot, AppPaths.UninstallRecoveryRoot, StringComparison.OrdinalIgnoreCase))
		{
			ValidateUninstallRecoveryArtifactsForPhase(uninstallRecoveryState);
		}
		return uninstallRecoveryState;
	}

	private static void FinalizeUninstallRecovery()
	{
		CleanupInterruptedUninstallTombstones();
		RuntimeSecurityService.NormalizeProtectedRollbackTree(AppPaths.UninstallRecoveryRoot);
		if (!MoveFileEx(AppPaths.UninstallRecoveryRoot, AppPaths.UninstallRecoveryCleanupRoot, 8u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久退休卸载恢复目录。");
		}
		RuntimeSecurityService.DeleteProtectedRollbackTree(AppPaths.UninstallRecoveryCleanupRoot);
	}

	private static void ValidateUninstallRecoveryState(UninstallRecoveryState state)
	{
		bool flag = state.Schema != 1;
		if (!flag)
		{
			bool flag2;
			switch (state.Phase)
			{
			case "prepared":
			case "snapshot-ready":
			case "commit-started":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = !flag2;
		}
		if (flag)
		{
			throw new InvalidDataException("卸载恢复状态 schema 或阶段无效。");
		}
		if (string.IsNullOrWhiteSpace(state.RuntimeUser))
		{
			throw new InvalidDataException("卸载恢复状态缺少运行账户。");
		}
		RuntimeSecurityService.ResolveInteractiveUserSid(state.RuntimeUser);
		if (state.WasRunning && !state.TaskWasPresent)
		{
			throw new InvalidDataException("无计划任务的卸载恢复状态不能声明原 Agent 正在运行。");
		}
		string[] source = new string[4] { state.ExpectedPeerId, state.ExpectedAgentVersion, state.ExpectedIdentitySha256, state.ExpectedTokenSha256 };
		if (state.WasRunning && source.Any(string.IsNullOrWhiteSpace))
		{
			throw new InvalidDataException("原 Agent 运行态恢复信息不完整。");
		}
		if (!state.WasRunning && source.Any((string value) => value != null))
		{
			throw new InvalidDataException("停止态卸载恢复状态不应包含运行态身份。");
		}
		if (state.WasRunning && (!AgentConfigService.IsValidPeerId(state.ExpectedPeerId) || !IsSha256(state.ExpectedIdentitySha256) || !IsSha256(state.ExpectedTokenSha256)))
		{
			throw new InvalidDataException("卸载恢复状态中的 PeerID 或 SHA-256 无效。");
		}
	}

	private static bool IsSha256(string value)
	{
		if (value.Length == 64)
		{
			return value.All(Uri.IsHexDigit);
		}
		return false;
	}

	private static void ValidateUninstallRecoveryArtifactsForPhase(UninstallRecoveryState state)
	{
		if (File.Exists(AppPaths.UninstallRecoveryBackupRoot) || File.Exists(AppPaths.UninstallRecoveryDataRoot))
		{
			throw new InvalidDataException("卸载恢复目录路径被普通文件占用。");
		}
		bool flag = Directory.Exists(AppPaths.UninstallRecoveryBackupRoot);
		if (state.Phase == "snapshot-ready" && !flag)
		{
			throw new InvalidDataException("卸载恢复阶段要求的停机快照缺失。");
		}
		if (flag)
		{
			if (state.Phase == "prepared")
			{
				RuntimeSecurityService.ValidateProtectedRollbackDirectory(AppPaths.UninstallRecoveryBackupRoot);
			}
			else
			{
				RuntimeSecurityService.ValidateProtectedRollbackTree(AppPaths.UninstallRecoveryBackupRoot);
				ValidateUninstallBackupManifest(state);
			}
		}
		bool flag2 = Directory.Exists("C:\\ProgramData\\P2PAgent");
		bool flag3 = Directory.Exists(AppPaths.UninstallRecoveryDataRoot);
		string phase = state.Phase;
		if ((phase == "prepared" || phase == "snapshot-ready") ? true : false)
		{
			if (flag3 || flag2 != state.DataRootWasPresent)
			{
				throw new InvalidDataException("提交前卸载恢复阶段与 DataRoot 存在性不一致。");
			}
		}
		else
		{
			if (!(state.Phase == "commit-started"))
			{
				return;
			}
			if (state.RemoveData)
			{
				if ((flag2 && flag3) || (!state.DataRootWasPresent && (flag2 || flag3)))
				{
					throw new InvalidDataException("前向提交阶段的 DataRoot/固定隔离路径关系无效。");
				}
			}
			else if (flag3 || flag2 != state.DataRootWasPresent)
			{
				throw new InvalidDataException("保留数据卸载已提交，但 DataRoot 存在性已变化。");
			}
		}
	}

	private static void ValidateUninstallBackupManifest(UninstallRecoveryState state)
	{
		string path = Path.Combine(AppPaths.UninstallRecoveryBackupRoot, "recovery-manifest.json");
		using JsonDocument jsonDocument = JsonDocument.Parse(RuntimeSecurityService.ReadProtectedRollbackTextFile(AppPaths.UninstallRecoveryBackupRoot, path, 262144));
		JsonElement rootElement = jsonDocument.RootElement;
		if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("schema", out var value) || value.GetInt32() != 1 || !rootElement.TryGetProperty("runtime_user", out var value2) || !string.Equals(value2.GetString(), state.RuntimeUser, StringComparison.OrdinalIgnoreCase) || !rootElement.TryGetProperty("firewall_rule_was_present", out var value3) || value3.GetBoolean() != state.FirewallRuleWasPresent)
		{
			throw new InvalidDataException("卸载停机快照 manifest 与受保护状态不一致。");
		}
		if (!rootElement.TryGetProperty("task", out var value4) || value4.GetProperty("present").GetBoolean() != state.TaskWasPresent || value4.GetProperty("enabled").GetBoolean() != state.TaskWasEnabled)
		{
			throw new InvalidDataException("卸载停机快照中的任务状态不匹配入口实际值。");
		}
		if (!rootElement.TryGetProperty("expected_runtime", out var value5))
		{
			throw new InvalidDataException("卸载停机快照缺少运行态字段。");
		}
		if (state.WasRunning)
		{
			if (value5.ValueKind != JsonValueKind.Object || !string.Equals(value5.GetProperty("peer_id").GetString(), state.ExpectedPeerId, StringComparison.Ordinal) || !string.Equals(value5.GetProperty("agent_version").GetString(), state.ExpectedAgentVersion, StringComparison.Ordinal) || !string.Equals(value5.GetProperty("identity_sha256").GetString(), state.ExpectedIdentitySha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(value5.GetProperty("token_sha256").GetString(), state.ExpectedTokenSha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("卸载停机快照中的运行态身份不匹配。");
			}
		}
		else if (value5.ValueKind != JsonValueKind.Null)
		{
			throw new InvalidDataException("停止态卸载快照不应包含运行态身份。");
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			AppPaths.AgentExe,
			AppPaths.ControlExe,
			AppPaths.ConfigFile,
			AppPaths.SwarmKeyFile,
			AppPaths.IdentityFile,
			AppPaths.ApiTokenFile,
			AppPaths.JournalFile,
			AppPaths.IdentityProvisioningMarker,
			AppPaths.TokenProvisioningMarker
		};
		if (!rootElement.TryGetProperty("files", out var value6) || value6.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidDataException("卸载停机快照缺少文件槽位。");
		}
		HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (JsonElement item in value6.EnumerateArray())
		{
			string text = item.GetProperty("target").GetString() ?? "";
			if (!hashSet.Contains(text) || !hashSet2.Add(text))
			{
				throw new InvalidDataException("卸载停机快照包含未知或重复目标：" + text);
			}
			if (item.GetProperty("present").GetBoolean())
			{
				string text2 = item.GetProperty("backup_file").GetString() ?? "";
				if (text2.Length == 0 || text2 != Path.GetFileName(text2) || !File.Exists(Path.Combine(AppPaths.UninstallRecoveryBackupRoot, text2)))
				{
					throw new InvalidDataException("卸载停机快照文件关联无效：" + text);
				}
			}
		}
		if (!hashSet2.SetEquals(hashSet))
		{
			throw new InvalidDataException("卸载停机快照文件目标集合不完整。");
		}
	}

	private static RuntimeIdentitySnapshot? ExpectedRuntimeFromState(UninstallRecoveryState state)
	{
		if (state.WasRunning)
		{
			return new RuntimeIdentitySnapshot(state.ExpectedPeerId, state.ExpectedAgentVersion, state.ExpectedIdentitySha256, state.ExpectedTokenSha256);
		}
		return null;
	}

	private static void QuarantineDataRootAfterCommit(UninstallRecoveryState state)
	{
		if (!state.RemoveData || state.Phase != "commit-started")
		{
			throw new InvalidOperationException("只有已持久提交的删除数据卸载可以隔离 DataRoot。");
		}
		bool num = Directory.Exists("C:\\ProgramData\\P2PAgent");
		bool flag = Directory.Exists(AppPaths.UninstallRecoveryDataRoot);
		if (File.Exists("C:\\ProgramData\\P2PAgent") || File.Exists(AppPaths.UninstallRecoveryDataRoot))
		{
			throw new IOException("DataRoot 或固定隔离路径被文件占用。");
		}
		if (num && flag)
		{
			throw new IOException("DataRoot 与固定隔离目录同时存在，拒绝猜测应删除哪一份。");
		}
		if (num)
		{
			if (!state.DataRootWasPresent)
			{
				throw new IOException("入口时不存在的 DataRoot 在卸载期间出现，拒绝隔离。");
			}
			RuntimeSecurityService.ValidateExistingDataRootTrust(state.RuntimeUser);
			RuntimeSecurityService.RejectReparsePoint("C:\\ProgramData\\P2PAgent");
			if (!MoveFileEx("C:\\ProgramData\\P2PAgent", AppPaths.UninstallRecoveryDataRoot, 8u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久隔离 DataRoot 到固定卸载恢复路径。 ");
			}
			flag = true;
		}
		if (flag)
		{
			if (!state.DataRootWasPresent)
			{
				throw new IOException("入口时不存在 DataRoot，但固定隔离目录出现，拒绝接管。");
			}
			RuntimeSecurityService.ProtectMovedDataRootForQuarantine(AppPaths.UninstallRecoveryDataRoot, state.RuntimeUser);
		}
	}

	private static void ValidateRollbackDataRootUnmoved(UninstallRecoveryState state)
	{
		bool flag = Directory.Exists("C:\\ProgramData\\P2PAgent");
		bool num = Directory.Exists(AppPaths.UninstallRecoveryDataRoot);
		if (File.Exists("C:\\ProgramData\\P2PAgent") || File.Exists(AppPaths.UninstallRecoveryDataRoot))
		{
			throw new IOException("DataRoot 或固定隔离路径被文件占用。");
		}
		if (num)
		{
			throw new IOException("提交前回滚阶段出现了固定隔离目录；拒绝执行反向目录移动。");
		}
		if (state.DataRootWasPresent)
		{
			if (!flag)
			{
				throw new IOException("入口 DataRoot 与固定隔离目录均缺失，无法回滚。");
			}
			RuntimeSecurityService.ValidateExistingDataRootTrust(state.RuntimeUser);
		}
		else if (flag)
		{
			throw new IOException("入口时不存在的 DataRoot 在恢复期间出现，拒绝接管。");
		}
	}

	public async Task<IReadOnlyList<InstallStep>> UninstallAsync(bool removeData, CancellationToken ct = default(CancellationToken))
	{
		if (HasInterruptedUninstallArtifacts)
		{
			throw new InvalidOperationException("检测到受保护的未完成卸载状态；请使用启动时的恢复入口继续或回滚，不能开始新的卸载事务。");
		}
		List<InstallStep> steps = new List<InstallStep>();
		ScheduledTaskInspection inspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (inspection.QueryFailed)
		{
			throw new InvalidOperationException("卸载已中止：无法确认计划任务状态。" + inspection.QueryError);
		}
		if (inspection.Exists && !inspection.MatchesExpectedDefinition)
		{
			throw new InvalidOperationException("卸载已中止：同名计划任务未通过精确定义校验，拒绝在事务中恢复或启动它。" + string.Join("；", inspection.Issues));
		}
		FirewallRuleInspection firewallRuleInspection = _firewall.Inspect();
		if (firewallRuleInspection.QueryFailed)
		{
			throw new InvalidOperationException("卸载已中止：无法确认 Windows Firewall 产品规则状态。" + firewallRuleInspection.QueryError);
		}
		if (firewallRuleInspection.Exists && !firewallRuleInspection.MatchesExpectedDefinition)
		{
			throw new InvalidOperationException("卸载已中止：同名 Windows Firewall 规则未通过精确定义校验，拒绝删除或在事务中恢复它。" + string.Join("；", firewallRuleInspection.Issues));
		}
		bool previousFirewallExpected = firewallRuleInspection.Exists;
		string originalDataUserSid = null;
		if (Directory.Exists("C:\\ProgramData\\P2PAgent"))
		{
			originalDataUserSid = RuntimeSecurityService.ResolveProtectedDataRootUserSid();
		}
		string taskXml = (inspection.Exists ? inspection.RawXml : null);
		bool taskEnabled = taskXml != null && inspection.EffectiveEnabled;
		bool taskMaintenanceMarkerPreexisted;
		bool desiredEnabled;
		try
		{
			taskMaintenanceMarkerPreexisted = RuntimeSecurityService.TryReadTaskMaintenanceMarker(out desiredEnabled);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException("卸载已中止：计划任务维护意图无法安全核验。" + ex.Message, ex);
		}
		TaskState taskState = await _task.GetStateAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		bool flag = ScheduledTaskService.IsAgentProcessRunning();
		bool wasRunning = taskXml != null && (taskState == TaskState.Running || flag);
		bool trustedAgent = IsTrustedRollbackAgent();
		if (flag && taskXml == null)
		{
			throw new InvalidOperationException("卸载已中止：检测到无法由受信任计划任务描述的现有 Agent 进程，停机后无法安全恢复。");
		}
		if (wasRunning && !trustedAgent)
		{
			throw new InvalidOperationException("卸载已中止：原 Agent 正在运行但不能证明其为可信发布者，无法安全满足失败回滚后的健康恢复要求。");
		}
		bool trustedConfig = false;
		bool trustedSwarm = false;
		try
		{
			if (_config.Exists)
			{
				AgentConfigService.ValidateRuntimeBoundary(_config.Load());
				trustedConfig = true;
			}
		}
		catch
		{
		}
		try
		{
			if (File.Exists(AppPaths.SwarmKeyFile))
			{
				RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
				trustedSwarm = true;
			}
		}
		catch
		{
		}
		if (wasRunning && (!trustedConfig || !trustedSwarm))
		{
			throw new InvalidOperationException("卸载已中止：原 Agent 正在运行，但磁盘上的配置或 swarm.key 不能作为可信回滚输入。");
		}
		RuntimeIdentitySnapshot expectedRuntime = await CaptureRunningRuntimeIdentityAsync(wasRunning, trustedAgent, ct).ConfigureAwait(continueOnCapturedContext: false);
		string runtimeUser = originalDataUserSid ?? ((!string.IsNullOrWhiteSpace(inspection.RunAsUser)) ? inspection.RunAsUser : App.InteractiveUserName);
		RuntimeSecurityService.EnsureSafeInstallRoot();
		DeploymentBackup backup = null;
		bool committed = false;
		bool taskMaintenanceMarkerOwned = false;
		UninstallRecoveryState recoveryState = CreateUninstallRecoveryState(removeData, originalDataUserSid != null, runtimeUser, taskXml != null, taskEnabled, wasRunning, previousFirewallExpected, taskMaintenanceMarkerPreexisted, expectedRuntime);
		InitializeUninstallRecoveryWithExecutionBoundary(recoveryState);
		try
		{
			if (taskXml != null && !taskMaintenanceMarkerPreexisted)
			{
				EnsureTaskMaintenanceMarker(taskEnabled);
				taskMaintenanceMarkerOwned = true;
			}
			backup = await StopAndCaptureBackupAsync(taskXml, taskEnabled, previousFirewallExpected, runtimeUser, trustedAgent, trustedConfig, trustedSwarm, null, wasRunning, expectedRuntime, ct, AppPaths.UninstallRecoveryBackupRoot).ConfigureAwait(continueOnCapturedContext: false);
			steps.Add(new InstallStep("创建回滚点", Success: true, backup.RootPath));
			steps.Add(new InstallStep("停止 Agent", Success: true, "已验证本产品宿主与 Agent 进程退出"));
			recoveryState = recoveryState with
			{
				Phase = "snapshot-ready"
			};
			WriteUninstallRecoveryState(recoveryState);
			recoveryState = recoveryState with
			{
				Phase = "commit-started"
			};
			WriteUninstallRecoveryState(recoveryState);
			committed = true;
			if (removeData)
			{
				QuarantineDataRootAfterCommit(recoveryState);
				steps.Add(new InstallStep("隔离运行数据", Success: true, recoveryState.DataRootWasPresent ? AppPaths.UninstallRecoveryDataRoot : "原本没有运行数据"));
			}
			List<InstallStep> list = steps;
			list.AddRange(await ResumeInterruptedUninstallAsync(ct).ConfigureAwait(continueOnCapturedContext: false));
			backup = null;
			taskMaintenanceMarkerOwned = false;
			return steps;
		}
		catch (Exception ex2)
		{
			if (committed || backup == null)
			{
				throw;
			}
			using CancellationTokenSource rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(180.0));
			List<string> rollbackErrors = new List<string>();
			bool rollbackStopConfirmed = false;
			try
			{
				await EstablishTaskMaintenanceMutationBoundaryAsync(runtimeUser, rollbackCts.Token)
					.ConfigureAwait(continueOnCapturedContext: false);
				rollbackStopConfirmed = true;
			}
			catch (Exception ex3)
			{
				rollbackErrors.Add("回滚 Mutation 执行屏障未建立：" + ex3.Message);
			}
			using CancellationTokenSource firewallRollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
			try
			{
				_firewall.RestoreTrustedState(backup.FirewallRuleWasPresent, firewallRollbackCts.Token);
			}
			catch (Exception ex4)
			{
				rollbackErrors.Add("Windows Firewall 规则恢复或复核失败：" + ex4.Message);
			}
			if (rollbackStopConfirmed)
			{
				try
				{
					await backup.RestoreAsync(taskXml != null && trustedAgent, rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					if (rollbackErrors.Count == 0 && taskXml != null)
					{
						ProcessResult processResult2 = await _task.RegisterXmlAsync(taskXml, rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
						if (!processResult2.Success)
						{
							throw new InvalidOperationException(processResult2.CombinedOutput);
						}
						ProcessResult processResult3 = await _task.SetEnabledAsync(taskEnabled, allowTaskMaintenance: true, rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
						if (!processResult3.Success)
						{
							throw new InvalidOperationException(processResult3.CombinedOutput);
						}
						ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync(rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
						if (scheduledTaskInspection.QueryFailed || !scheduledTaskInspection.Exists || !scheduledTaskInspection.MatchesExpectedDefinition || scheduledTaskInspection.EffectiveEnabled != taskEnabled)
						{
							throw new InvalidOperationException("原任务定义或 Enabled 状态恢复复核失败。");
						}
					}
					if (rollbackErrors.Count == 0 && wasRunning)
					{
						await StartAndValidateRestoredRuntimeAsync(backup.TaskWasEnabled, backup.RuntimeUser, backup.ExpectedRuntime ?? throw new InvalidDataException("缺少原运行态身份快照。"), rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				catch (Exception ex5)
				{
					rollbackErrors.Add(ex5.Message);
				}
			}
			if (rollbackErrors.Count > 0)
			{
				try
				{
					ProcessResult processResult4 = await _task.DeleteAsync(rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					if (!processResult4.Success)
					{
						rollbackErrors.Add("不完整回滚后的任务删除失败：" + processResult4.CombinedOutput);
					}
					ScheduledTaskInspection scheduledTaskInspection2 = await _task.InspectAsync(rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					if (scheduledTaskInspection2.QueryFailed || scheduledTaskInspection2.Exists)
					{
						rollbackErrors.Add("不完整回滚后的任务不存在性复核失败：" + scheduledTaskInspection2.QueryError);
					}
				}
				catch (Exception ex6)
				{
					rollbackErrors.Add("不完整回滚后的任务删除失败：" + ex6.Message);
				}
			}
			bool flag2 = rollbackErrors.Count == 0;
			if (flag2)
			{
				try
				{
					if (taskMaintenanceMarkerOwned)
					{
						DeleteTaskMaintenanceMarkerRequired();
					}
					else if (taskMaintenanceMarkerPreexisted)
					{
						if (!RuntimeSecurityService.TryReadTaskMaintenanceMarker(out desiredEnabled))
						{
							throw new InvalidDataException("入口时已有的计划任务维护意图在卸载回滚期间丢失。 ");
						}
						await EstablishTaskMaintenanceMutationBoundaryAsync(runtimeUser, rollbackCts.Token)
							.ConfigureAwait(continueOnCapturedContext: false);
					}
					FinalizeUninstallRecovery();
					if (!taskMaintenanceMarkerPreexisted)
					{
						if (RuntimeSecurityService.HasMaintenanceArtifacts)
						{
							throw new InvalidDataException("卸载回滚后仍存在活动维护工件，拒绝恢复普通执行权限。 ");
						}
						RestoreNormalAgentExecutionAclIfRestricted();
					}
				}
				catch (Exception ex7)
				{
					rollbackErrors.Add("卸载已回滚，但受保护恢复状态清理未完成：" + ex7.Message);
				}
			}
			if (rollbackErrors.Count == 0)
			{
				steps.Add(new InstallStep("卸载回滚", Success: true, "已恢复文件、任务、Enabled 偏好及原运行状态并完成健康验证"));
			}
			else
			{
				if (!flag2)
				{
					try
					{
						await _task.StopAsync(rollbackCts.Token).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch
					{
					}
				}
				steps.Add(new InstallStep("卸载回滚", Success: false, string.Join("；", rollbackErrors) + "；受保护恢复状态保留：" + AppPaths.UninstallRecoveryRoot));
			}
			throw new InvalidOperationException("卸载事务失败：" + ex2.Message + "；" + steps.Last((InstallStep step) => step.Title == "卸载回滚").Detail, ex2);
		}
	}

	private static void ValidateInstallOptions(InstallOptions options)
	{
		RuntimeSecurityService.ResolveInteractiveUserSid(options.RunAsUser);
		string text = options.AgentName.Trim();
		int length = text.Length;
		bool flag = ((length < 1 || length > 128) ? true : false);
		if (flag || text.Any(char.IsControl))
		{
			throw new InvalidDataException("Agent 名称必须为 1–128 个非控制字符。");
		}
		length = options.MaxParallelTasks;
		if ((length < 1 || length > 64) ? true : false)
		{
			throw new InvalidDataException("并行任务数必须为 1–64。");
		}
		long maxTransferBytes = options.MaxTransferBytes;
		if ((maxTransferBytes < 1 || maxTransferBytes > 1099511627776L) ? true : false)
		{
			throw new InvalidDataException("传输上限必须为 1 字节至 1 TiB。");
		}
		if (!AgentConfigService.IsValidRendezvousGroup(options.RendezvousGroup))
		{
			throw new InvalidDataException("发现组不能为空或含控制字符，且 UTF-8 最长 256 字节。");
		}
		if (options.BootstrapAddrs.Count > 32 || options.BootstrapAddrs.Any((string address) => !AgentConfigService.LooksLikeBootstrapMultiaddr(address)))
		{
			throw new InvalidDataException("bootstrap_addrs 数量超过 32 或包含无效 libp2p multiaddr。");
		}
		AgentConfigService.SetAllowedPeers(new JsonObject(), options.AllowedPeers);
	}

	private static async Task<string> StageAndValidateAgentAsync(CancellationToken ct)
	{
		string stage = Path.Combine("C:\\Program Files\\P2PAgent", $"p2p-agent.exe.{Guid.NewGuid():N}.stage.exe");
		ExtractResource("ZhanClawControl.payload.p2p-agent.exe", stage);
		try
		{
			await RuntimeSecurityService.ValidateAgentPayloadAsync(stage, ct).ConfigureAwait(continueOnCapturedContext: false);
			RuntimeSecurityService.RestrictAgentExecutionForMaintenance(stage);
			return stage;
		}
		catch
		{
			TryDelete(stage);
			throw;
		}
	}

	private static void EnsureIdentityProvisioningMarker()
	{
		EnsureProvisioningMarker(AppPaths.IdentityProvisioningMarker, "ZhanClawControl identity provisioning v1\n", RuntimeSecurityService.HasValidIdentityProvisioningMarker, "首次身份许可");
	}

	private static void EnsureTokenProvisioningMarker()
	{
		EnsureProvisioningMarker(AppPaths.TokenProvisioningMarker, "ZhanClawControl api token provisioning v1\n", RuntimeSecurityService.HasValidTokenProvisioningMarker, "API Token 轮换许可");
	}

	private static void EnsureTaskMaintenanceMarker(bool desiredEnabled)
	{
		RuntimeSecurityService.CleanupTaskMaintenanceMarkerTombstone();
		EnsureProvisioningMarker(AppPaths.TaskMaintenanceMarker,
			desiredEnabled ? AppPaths.TaskMaintenanceMutationEnabledContent : AppPaths.TaskMaintenanceMutationDisabledContent,
			() => RuntimeSecurityService.TryReadTaskMaintenanceMarker(out var desiredEnabled2,
				out RuntimeSecurityService.TaskMaintenancePhase phase) &&
				desiredEnabled2 == desiredEnabled &&
				phase == RuntimeSecurityService.TaskMaintenancePhase.Mutation,
			"计划任务维护意图");
	}

	private static void EstablishTaskMaintenanceExecutionBoundary(bool desiredEnabled)
	{
		RuntimeSecurityService.RestrictAgentExecutionForMaintenance(AppPaths.AgentExe);
		try
		{
			RuntimeSecurityService.RestoreTaskMaintenanceMutationPhaseIfPresent();
			EnsureTaskMaintenanceMarker(desiredEnabled);
		}
		catch (Exception innerException)
		{
			if (!RuntimeSecurityService.HasMaintenanceArtifacts)
			{
				try
				{
					RestoreNormalAgentExecutionAclIfRestricted();
				}
				catch (Exception ex)
				{
					throw new IOException("计划任务维护意图发布失败，且 Agent 普通执行 ACL 无法恢复：" + ex.Message, innerException);
				}
			}
			throw;
		}
	}

	private static void DeleteEmptyProtectedRuntimeFileForProvisioning(string path, string description)
	{
		if (!File.Exists(path))
		{
			return;
		}
		using (FileStream fileStream = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(path))
		{
			if (fileStream.Length != 0L)
			{
				throw new InvalidDataException(description + " 不是空文件，拒绝把已有凭据当作首次生成目标删除。");
			}
		}
		RuntimeSecurityService.RejectReparsePoint(path);
		File.Delete(path);
		if (!File.Exists(path))
		{
			return;
		}
		throw new IOException("空的 " + description + " 删除后仍存在。");
	}

	private static void EnsureProvisioningMarker(string markerPath, string markerContent, Func<bool> validate, string description)
	{
		RuntimeSecurityService.EnsureSafeInstallRoot();
		if (File.Exists(markerPath))
		{
			if (!validate())
			{
				throw new InvalidDataException("现有" + description + "无效。");
			}
			return;
		}
		string text = Path.Combine("C:\\Program Files\\P2PAgent", $".runtime-provisioning-{Guid.NewGuid():N}.stage");
		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes(markerContent);
			using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None, bytes.Length, FileOptions.WriteThrough))
			{
				fileStream.Write(bytes);
				fileStream.Flush(flushToDisk: true);
			}
			RuntimeSecurityService.RejectReparsePoint(text);
			if (!MoveFileEx(text, markerPath, 9u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error(), "无法持久发布" + description + "。");
			}
			if (!validate())
			{
				throw new InvalidDataException("无法读回核验" + description + "。");
			}
		}
		finally
		{
			TryDelete(text);
		}
	}

	private static void DeleteIdentityProvisioningMarkerRequired()
	{
		DeleteProvisioningMarkerRequired(AppPaths.IdentityProvisioningMarker, "首次身份许可");
	}

	private static void DeleteTokenProvisioningMarkerRequired()
	{
		DeleteProvisioningMarkerRequired(AppPaths.TokenProvisioningMarker, "API Token 轮换许可");
	}

	private static void DeleteTaskMaintenanceMarkerRequired()
	{
		RuntimeSecurityService.RetireTaskMaintenanceMarker();
	}

	private static void DeleteProvisioningMarkerRequired(string markerPath, string description)
	{
		if (File.Exists(markerPath))
		{
			RuntimeSecurityService.RejectReparsePoint(markerPath);
			File.Delete(markerPath);
			if (File.Exists(markerPath))
			{
				throw new IOException(description + "删除后仍存在。");
			}
		}
	}

	private static void TryDeleteIdentityProvisioningMarker()
	{
		try
		{
			DeleteIdentityProvisioningMarkerRequired();
		}
		catch
		{
		}
	}

	private static void TryDeleteTokenProvisioningMarker()
	{
		try
		{
			DeleteTokenProvisioningMarkerRequired();
		}
		catch
		{
		}
	}

	private static void TryDeleteTaskMaintenanceMarker()
	{
		try
		{
			DeleteTaskMaintenanceMarkerRequired();
		}
		catch
		{
		}
	}

	private static string GetEmbeddedSwarmKeySha256()
	{
		using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZhanClawControl.payload.swarm.key") ?? throw new FileNotFoundException("安装包缺少内嵌 swarm.key，不能执行旧版 ACL 安全迁移。");
		return Convert.ToHexString(SHA256.HashData(source));
	}

	private static string StageControlExecutable()
	{
		string processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
		{
			throw new FileNotFoundException("无法定位控制软件自身路径。");
		}
		RuntimeSecurityService.RejectReparsePoint(processPath);
		string text = Path.Combine("C:\\Program Files\\P2PAgent", $"ZhanClawControl.{Guid.NewGuid():N}.stage.exe");
		File.Copy(processPath, text, overwrite: false);
		return text;
	}

	private static string StageSwarmKey(string? selectedPath)
	{
		string text = Path.Combine("C:\\Program Files\\P2PAgent", $"swarm.key.{Guid.NewGuid():N}.stage");
		if (HasEmbeddedSwarmKey)
		{
			ExtractResource("ZhanClawControl.payload.swarm.key", text);
		}
		else
		{
			if (string.IsNullOrWhiteSpace(selectedPath))
			{
				throw new FileNotFoundException("没有已有或可用的 swarm.key。");
			}
			RuntimeSecurityService.RejectReparsePoint(selectedPath);
			File.Copy(selectedPath, text, overwrite: false);
		}
		return text;
	}

	private static void ExtractResource(string resourceName, string targetPath)
	{
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException("安装包缺少嵌入资源：" + resourceName);
		using FileStream fileStream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		stream.CopyTo(fileStream);
		fileStream.Flush(flushToDisk: true);
	}

	private static void AtomicReplace(string source, string target)
	{
		RuntimeSecurityService.RejectReparsePoint(source);
		if (File.Exists(target))
		{
			RuntimeSecurityService.RejectReparsePoint(target);
		}
		File.Move(source, target, overwrite: true);
	}

	private static void DeployControlExecutable(string stagedControl)
	{
		if (File.Exists(AppPaths.ControlExe) && FilesHaveSameContent(stagedControl, AppPaths.ControlExe))
		{
			TryDelete(stagedControl);
			return;
		}
		string processPath = Environment.ProcessPath;
		if (processPath != null && PathsEqual(processPath, AppPaths.ControlExe))
		{
			throw new IOException("当前控制程序正从安装目录运行且内容需要更新，不能安全覆盖已映射的 EXE。请从新安装包副本执行修复。");
		}
		AtomicReplace(stagedControl, AppPaths.ControlExe);
	}

	private static bool PathsEqual(string left, string right)
	{
		try
		{
			return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static bool FilesHaveSameContent(string left, string right)
	{
		if (new FileInfo(left).Length != new FileInfo(right).Length)
		{
			return false;
		}
		using FileStream source = File.OpenRead(left);
		using FileStream source2 = File.OpenRead(right);
		return SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(source2));
	}

	private static async Task<bool> FilesHaveSameContentAsync(string left, string right, CancellationToken ct)
	{
		if (new FileInfo(left).Length != new FileInfo(right).Length)
		{
			return false;
		}
		bool result;
		await using (FileStream a = File.OpenRead(left))
		{
			bool flag;
			await using (FileStream b = File.OpenRead(right))
			{
				byte[] ah = await SHA256.HashDataAsync(a, ct).ConfigureAwait(continueOnCapturedContext: false);
				byte[] array = await SHA256.HashDataAsync(b, ct).ConfigureAwait(continueOnCapturedContext: false);
				flag = ah.AsSpan().SequenceEqual(array);
			}
			result = flag;
		}
		return result;
	}

	private static void CleanupLegacyLauncher()
	{
		try
		{
			if (File.Exists(AppPaths.LegacyLauncherCmd))
			{
				RuntimeSecurityService.RejectReparsePoint(AppPaths.LegacyLauncherCmd);
				File.Delete(AppPaths.LegacyLauncherCmd);
			}
		}
		catch
		{
		}
	}

	private static bool IsTrustedRollbackAgent()
	{
		if (!File.Exists(AppPaths.AgentExe))
		{
			return false;
		}
		try
		{
			RuntimeSecurityService.ValidateTrustedAgentPublisherForRollback(AppPaths.AgentExe);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static async Task<RuntimeIdentitySnapshot?> CaptureRunningRuntimeIdentityAsync(bool wasRunning, bool trustedAgent, CancellationToken ct)
	{
		if (!wasRunning)
		{
			return null;
		}
		if (!trustedAgent)
		{
			throw new InvalidOperationException("现有 Agent 正在运行，但其发布者不能作为可信回滚输入。");
		}
		using ControlApiClient client = new ControlApiClient();
		AgentInfo info = await client.GetInfoAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if ((object)info == null || string.IsNullOrWhiteSpace(info.PeerId))
		{
			throw new InvalidOperationException("现有 Agent 正在运行，但无法通过当前 Token 获取其 PeerID；为避免不可验证的停机回滚，操作已中止。");
		}
		(string Sha256, long Length) identity;
		(string, long) tuple;
		try
		{
			identity = await HashFileBoundedAsync(AppPaths.IdentityFile, ct).ConfigureAwait(continueOnCapturedContext: false);
			tuple = await HashFileBoundedAsync(AppPaths.ApiTokenFile, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
		{
			throw new InvalidDataException("现有 Agent 正在运行，但身份文件或本机 API Token 缺失，无法记录可核验的回滚身份。", ex);
		}
		return new RuntimeIdentitySnapshot(info.PeerId, info.Version, identity.Sha256, tuple.Item1);
	}

	private async Task<DeploymentBackup> StopAndCaptureBackupAsync(string? taskXml, bool taskWasEnabled, bool firewallRuleWasPresent, string runtimeUser, bool includeRunnableAgent, bool includeConfig, bool includeSwarm, Action? stoppedPreCapture, bool wasRunning, RuntimeIdentitySnapshot? expectedRuntime, CancellationToken ct, string? backupRoot = null)
	{
		if (taskXml != null)
		{
			ProcessResult processResult = await _task.SetEnabledAsync(enabled: false, allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult.Success)
			{
				InvalidOperationException ex = new InvalidOperationException("无法在停机快照期间禁用登录触发任务：" + processResult.CombinedOutput);
				await ThrowAfterStopCaptureRecoveryAsync(ex.Message, ex, taskXml, taskWasEnabled, runtimeUser, wasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
				throw new UnreachableException();
			}
		}
		ProcessResult stop;
		try
		{
			stop = await _task.StopAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex2)
		{
			await ThrowAfterStopCaptureRecoveryAsync("无法确认旧 Agent 已停止，未创建可能不一致的回滚点：" + ex2.Message, ex2, taskXml, taskWasEnabled, runtimeUser, wasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
			throw new UnreachableException();
		}
		if (!stop.Success)
		{
			InvalidOperationException cause = new InvalidOperationException(stop.CombinedOutput);
			await ThrowAfterStopCaptureRecoveryAsync("无法确认旧 Agent 已停止，未创建可能不一致的回滚点：" + stop.CombinedOutput, cause, taskXml, taskWasEnabled, runtimeUser, wasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
			throw new UnreachableException();
		}
		try
		{
			RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(runtimeUser);
			RuntimeSecurityService.RestrictAgentExecutionForMaintenance(AppPaths.AgentExe);
			ProcessResult processResult2 = await _task.StopAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult2.Success)
			{
				throw new InvalidOperationException(processResult2.CombinedOutput);
			}
			RuntimeSecurityService.RestoreTaskMaintenanceMutationPhaseIfPresent();
		}
		catch (Exception ex3)
		{
			await ThrowAfterStopCaptureRecoveryAsync("无法建立维护执行屏障：" + ex3.Message, ex3, taskXml, taskWasEnabled, runtimeUser, wasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
			throw new UnreachableException();
		}
		DeploymentBackup partial = null;
		try
		{
			stoppedPreCapture?.Invoke();
			if (Directory.Exists("C:\\ProgramData\\P2PAgent"))
			{
				RuntimeSecurityService.ValidateExistingDataRootTrustAllowingLegacyEmbeddedSwarm(runtimeUser, GetEmbeddedSwarmKeySha256());
			}
			partial = await CreateBackupAsync(taskXml, taskWasEnabled, firewallRuleWasPresent, runtimeUser, includeRunnableAgent, includeConfig, includeSwarm, expectedRuntime, ct, backupRoot).ConfigureAwait(continueOnCapturedContext: false);
			return partial;
		}
		catch (Exception ex4)
		{
			string text = ((partial != null && !partial.Delete()) ? ("；不完整的受保护恢复目录无法删除：" + partial.RootPath) : "");
			await ThrowAfterStopCaptureRecoveryAsync("停机后一致性回滚材料捕获失败：" + ex4.Message + text, ex4, taskXml, taskWasEnabled, runtimeUser, wasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
			throw new UnreachableException();
		}
	}

	private async Task EstablishTaskMaintenanceMutationBoundaryAsync(string runtimeUser, CancellationToken ct)
	{
		ProcessResult firstStop = await _task.StopAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!firstStop.Success)
		{
			throw new InvalidOperationException("无法确认本产品 Host/Agent 已停止：" + firstStop.CombinedOutput);
		}
		RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(runtimeUser);
		RuntimeSecurityService.RestrictAgentExecutionForMaintenance(AppPaths.AgentExe);
		// A raw Agent may have started between the first stop and ACL update.
		// Recheck absence after execute permission is restricted, then publish
		// Mutation with a write-through rename before any protected file changes.
		ProcessResult secondStop = await _task.StopAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!secondStop.Success)
		{
			throw new InvalidOperationException("限制 Agent 执行权限后仍无法确认 Host/Agent 不存在：" + secondStop.CombinedOutput);
		}
		RuntimeSecurityService.RestoreTaskMaintenanceMutationPhaseIfPresent();
	}

	private async Task ThrowAfterStopCaptureRecoveryAsync(string failureDetail, Exception cause, string? taskXml, bool taskWasEnabled, string runtimeUser, bool wasRunning, RuntimeIdentitySnapshot? expectedRuntime)
	{
		(bool, string) tuple = await RestoreAfterCaptureFailureAsync(taskXml, taskWasEnabled, runtimeUser, wasRunning, expectedRuntime).ConfigureAwait(continueOnCapturedContext: false);
		if (!tuple.Item1)
		{
			throw new StopCaptureRecoveryFailedException(failureDetail + "；原任务/运行态恢复未通过核验：" + tuple.Item2, cause);
		}
		throw new StopCaptureRecoveredException(failureDetail + "；原任务定义、Enabled 偏好及运行态已完整恢复并核验", cause);
	}

	private async Task<(bool Success, string Detail)> RestoreAfterCaptureFailureAsync(string? taskXml, bool taskWasEnabled, string runtimeUser, bool wasRunning, RuntimeIdentitySnapshot? expectedRuntime)
	{
		using CancellationTokenSource restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(45.0));
		CancellationToken ct = restoreCts.Token;
		try
		{
			if (taskXml != null)
			{
				ProcessResult processResult = await _task.SetEnabledAsync(taskWasEnabled, allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!processResult.Success)
				{
					return (Success: false, Detail: "无法恢复原计划任务 Enabled 偏好：" + processResult.CombinedOutput);
				}
			}
			ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (taskXml == null)
			{
				if (scheduledTaskInspection.QueryFailed || scheduledTaskInspection.Exists)
				{
					return (Success: false, Detail: "原本不存在的任务在捕获失败后出现，状态不可信：" + scheduledTaskInspection.QueryError);
				}
				if (wasRunning)
				{
					return (Success: false, Detail: "原 Agent 曾运行，但没有可信任务可用于恢复。");
				}
				if (ScheduledTaskService.IsAgentProcessRunning())
				{
					return (Success: false, Detail: "原本停止的 Agent 在恢复核验时仍有精确路径进程，运行态不可信。");
				}
				return (Success: true, Detail: "原任务不存在且没有运行态需要恢复");
			}
			if (scheduledTaskInspection.QueryFailed || !scheduledTaskInspection.Exists || !scheduledTaskInspection.MatchesExpectedDefinition || scheduledTaskInspection.EffectiveEnabled != taskWasEnabled)
			{
				return (Success: false, Detail: "停机后原任务定义或 Enabled 偏好不再匹配：" + string.Join("；", from value in scheduledTaskInspection.Issues.Append(scheduledTaskInspection.QueryError)
					where !string.IsNullOrWhiteSpace(value)
					select value));
			}
			if (!wasRunning)
			{
				TaskState taskState = await _task.GetStateAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				TaskState taskState2 = (taskWasEnabled ? TaskState.Ready : TaskState.Disabled);
				if (taskState != taskState2 || ScheduledTaskService.IsAgentProcessRunning())
				{
					return (Success: false, Detail: $"原任务停止态复核不匹配（expected={taskState2}, actual={taskState}）。");
				}
				return (Success: true, Detail: "原任务保持停止状态且定义未变化");
			}
			await StartAndValidateRestoredRuntimeAsync(taskWasEnabled, runtimeUser, expectedRuntime ?? throw new InvalidDataException("缺少原运行态身份快照。"), ct).ConfigureAwait(continueOnCapturedContext: false);
			return (Success: true, Detail: "原 Agent 已恢复且 PeerID/Token/identity 均匹配");
		}
		catch (Exception ex)
		{
			return (Success: false, Detail: ex.Message);
		}
	}

	private static async Task<DeploymentBackup> CreateBackupAsync(string? taskXml, bool taskWasEnabled, bool firewallRuleWasPresent, string runtimeUser, bool includeRunnableAgent, bool includeConfig, bool includeSwarm, RuntimeIdentitySnapshot? expectedRuntime, CancellationToken ct, string? backupRoot = null)
	{
		DeploymentBackup backup = new DeploymentBackup(taskXml, taskWasEnabled, firewallRuleWasPresent, runtimeUser, expectedRuntime, backupRoot);
		try
		{
			await backup.CaptureAsync(AppPaths.AgentExe, BackupFileRole.Agent, includeRunnableAgent, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.ControlExe, BackupFileRole.Control, restoreAllowed: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.ConfigFile, BackupFileRole.Config, includeConfig, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.SwarmKeyFile, BackupFileRole.Swarm, includeSwarm, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.IdentityFile, BackupFileRole.Identity, restoreAllowed: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.ApiTokenFile, BackupFileRole.Token, restoreAllowed: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.JournalFile, BackupFileRole.Journal, restoreAllowed: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.IdentityProvisioningMarker, BackupFileRole.IdentityProvisioningMarker, restoreAllowed: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.CaptureAsync(AppPaths.TokenProvisioningMarker, BackupFileRole.TokenProvisioningMarker, restoreAllowed: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			await backup.ValidateCapturedTargetsStillMatchAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			backup.ValidateExpectedRuntimeSnapshot();
			await backup.WriteRecoveryManifestAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			RuntimeSecurityService.ProtectRollbackTree(backup.RootPath);
			return backup;
		}
		catch (Exception ex)
		{
			if (!backup.Delete())
			{
				throw new IOException("创建回滚点失败且无法清理敏感备份：" + backup.RootPath, ex);
			}
			throw new IOException("创建停机后一致性回滚点失败：" + ex.Message, ex);
		}
	}

	private static Task ValidateRuntimeIdentityAsync(RuntimeIdentitySnapshot expected, CancellationToken ct)
	{
		return ValidateRuntimeIdentityAsync(expected, ct, requireVersionMatch: true);
	}

	private static async Task ValidateRuntimeIdentityAsync(RuntimeIdentitySnapshot expected, CancellationToken ct, bool requireVersionMatch)
	{
		(string Sha256, long Length) identity = await HashFileBoundedAsync(AppPaths.IdentityFile, ct).ConfigureAwait(continueOnCapturedContext: false);
		(string, long) obj = await HashFileBoundedAsync(AppPaths.ApiTokenFile, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!string.Equals(identity.Sha256, expected.IdentitySha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("恢复后的 agent-identity.key 与停机快照不一致。");
		}
		if (!string.Equals(obj.Item1, expected.TokenSha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("恢复后的 agent-api.token 与停机快照不一致。");
		}
		using ControlApiClient client = new ControlApiClient();
		AgentInfo agentInfo = await client.GetInfoAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		if ((object)agentInfo == null || !string.Equals(agentInfo.PeerId, expected.PeerId, StringComparison.Ordinal))
		{
			throw new InvalidDataException("恢复后的鉴权 API PeerID 与停机前运行态不一致。");
		}
		if (requireVersionMatch && !string.Equals(agentInfo.Version, expected.AgentVersion, StringComparison.Ordinal))
		{
			throw new InvalidDataException("恢复后的 Agent 版本声明与停机前运行态不一致。");
		}
	}

	private static void ValidateGeneratedRuntimeState(string runAsUser)
	{
		RuntimeSecurityService.ValidateExistingDataRootTrust(runAsUser);
		using FileStream fileStream = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(AppPaths.IdentityFile);
		using FileStream fileStream2 = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(AppPaths.ApiTokenFile);
		if (fileStream.Length == 0L)
		{
			throw new InvalidDataException("Agent 已就绪，但首次运行生成的 agent-identity.key 为空。");
		}
		if (fileStream2.Length == 0L)
		{
			throw new InvalidDataException("Agent 已就绪，但首次运行生成的 agent-api.token 为空。");
		}
		try
		{
			using FileStream fileStream3 = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(AppPaths.JournalFile);
			_ = fileStream3.Length;
		}
		catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
		{
		}
	}

	private static bool HasNonEmptyProtectedRuntimeFile(string path)
	{
		try
		{
			using FileStream fileStream = RuntimeSecurityService.OpenProtectedRuntimeFileForRead(path);
			return fileStream.Length > 0;
		}
		catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
		{
			return false;
		}
	}

	private static void RestoreNormalAgentExecutionAclIfRestricted()
	{
		if (File.Exists(AppPaths.AgentExe) && RuntimeSecurityService.IsAgentExecutionRestricted(AppPaths.AgentExe))
		{
			RuntimeSecurityService.RestoreAgentExecutionForControlledStart(AppPaths.AgentExe);
		}
	}

	private static bool IsMissingOrEmptyForDeploymentReport(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return true;
			}
			RuntimeSecurityService.RejectReparsePoint(path);
			return new FileInfo(path).Length == 0;
		}
		catch
		{
			return false;
		}
	}

	private async Task StartAndValidateRestoredRuntimeAsync(bool taskWasEnabled, string runtimeUser, RuntimeIdentitySnapshot expected, CancellationToken ct)
	{
		try
		{
			ProcessResult processResult = await _task.SetEnabledAsync(enabled: false, allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult.Success)
			{
				throw new InvalidOperationException("原 Agent 恢复启动前无法保持任务 disabled：" + processResult.CombinedOutput);
			}
			ProcessResult processResult2 = await _task.StartAsync(allowTaskMaintenance: true, ct, allowTrustedRollbackPayload: true).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult2.Success)
			{
				throw new InvalidOperationException("原 Agent 恢复启动失败：" + processResult2.CombinedOutput);
			}
			if (!(await WaitForReadyAsync(TimeSpan.FromSeconds(30.0), expected.AgentVersion, ct).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new InvalidOperationException("原 Agent 恢复启动后未通过本机鉴权 API 健康验证。");
			}
			ValidateGeneratedRuntimeState(runtimeUser);
			await ValidateRuntimeIdentityAsync(expected, ct).ConfigureAwait(continueOnCapturedContext: false);
			ProcessResult processResult3 = await _task.SetEnabledAsync(taskWasEnabled, allowTaskMaintenance: true, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!processResult3.Success)
			{
				throw new InvalidOperationException("原 Agent 恢复启动后无法恢复任务 Enabled 偏好：" + processResult3.CombinedOutput);
			}
			ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (scheduledTaskInspection.QueryFailed || !scheduledTaskInspection.Exists || !scheduledTaskInspection.MatchesExpectedDefinition || scheduledTaskInspection.EffectiveEnabled != taskWasEnabled)
			{
				throw new InvalidOperationException("恢复启动后原任务定义或 Enabled 偏好发生变化：" + string.Join("；", from value in scheduledTaskInspection.Issues.Append(scheduledTaskInspection.QueryError)
					where !string.IsNullOrWhiteSpace(value)
					select value));
			}
			// Exact-process observation does not itself prove that this launch's Host
			// consumed the one-shot permit (for example, a pre-existing exact Agent may
			// have won the observation race). Make absence of the permit part of the
			// restored-runtime commit gate before any caller retires its task marker.
			RuntimeSecurityService.DeleteMaintenanceStartPermitIfPresent(runtimeUser);
		}
		catch (Exception ex)
		{
			string cleanupDetail;
			try
			{
				using CancellationTokenSource cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0));
				await EstablishTaskMaintenanceMutationBoundaryAsync(runtimeUser, cleanupCts.Token)
					.ConfigureAwait(continueOnCapturedContext: false);
				cleanupDetail = "已停止失败实例、限制 Agent 执行 ACL 并持久恢复 Mutation 阶段";
			}
			catch (Exception cleanupError)
			{
				cleanupDetail = "失败启动后的 Mutation 执行屏障未完整恢复：" + cleanupError.Message;
			}
			throw new InvalidOperationException(ex.Message + "；" + cleanupDetail, ex);
		}
	}

	private static async Task<(string Sha256, long Length)> HashFileBoundedAsync(string path, CancellationToken ct)
	{
		(string Sha256, long Length) result;
		await using (FileStream stream = OpenRecoverySource(path))
		{
			long length = stream.Length;
			if (length > 536870912)
			{
				throw new IOException($"恢复材料单文件超过 {512} MiB 上限：{path}（{length} 字节）");
			}
			string item = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(continueOnCapturedContext: false));
			if (stream.Length != length)
			{
				throw new IOException("计算恢复材料哈希期间文件长度发生变化：" + path);
			}
			result = (Sha256: item, Length: length);
		}
		return result;
	}

	private static FileStream OpenRecoverySource(string path)
	{
		if (IsProtectedRuntimePath(path))
		{
			return RuntimeSecurityService.OpenProtectedRuntimeFileForRead(path);
		}
		RuntimeSecurityService.RejectReparsePoint(path);
		return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
	}

	private static bool IsProtectedRuntimePath(string path)
	{
		return new string[5]
		{
			AppPaths.ConfigFile,
			AppPaths.SwarmKeyFile,
			AppPaths.IdentityFile,
			AppPaths.ApiTokenFile,
			AppPaths.JournalFile
		}.Any((string candidate) => PathsEqual(path, candidate));
	}

	private async Task<(bool Success, string Detail)> RollbackAsync(DeploymentBackup backup, string? previousTaskXml, bool wasRunning)
	{
		List<string> errors = new List<string>();
		using CancellationTokenSource rollbackCts = new CancellationTokenSource(TimeSpan.FromSeconds(180.0));
		CancellationToken ct = rollbackCts.Token;
		try
		{
			await EstablishTaskMaintenanceMutationBoundaryAsync(backup.RuntimeUser, ct)
				.ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			string text = "回滚维护执行屏障未建立，禁止覆盖任何文件：" + ex.Message;
			using CancellationTokenSource firewallCts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
			try
			{
				_firewall.RestoreTrustedState(backup.FirewallRuleWasPresent, firewallCts.Token);
			}
			catch (Exception firewallError)
			{
				text += "；Windows Firewall 规则恢复或复核失败：" + firewallError.Message;
			}
			return (Success: false, Detail: text + "；回滚备份已保留：" + backup.RootPath);
		}
		try
		{
			await backup.RestoreAsync(previousTaskXml != null, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex5)
		{
			errors.Add("文件恢复或恢复后完整性校验失败：" + ex5.Message);
		}
		using CancellationTokenSource firewallRestoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
		try
		{
			_firewall.RestoreTrustedState(backup.FirewallRuleWasPresent, firewallRestoreCts.Token);
		}
		catch (Exception ex6)
		{
			errors.Add("Windows Firewall 规则恢复或复核失败：" + ex6.Message);
		}
		try
		{
			if (errors.Count > 0 || previousTaskXml == null)
			{
				ProcessResult processResult2 = await _task.DeleteAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!processResult2.Success)
				{
					errors.Add("删除新任务失败：" + processResult2.CombinedOutput);
				}
				ScheduledTaskInspection scheduledTaskInspection = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (scheduledTaskInspection.QueryFailed || scheduledTaskInspection.Exists)
				{
					errors.Add("删除新任务后的复核失败：" + scheduledTaskInspection.QueryError);
				}
			}
			else
			{
				ProcessResult processResult3 = await _task.RegisterXmlAsync(previousTaskXml, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!processResult3.Success)
				{
					errors.Add("任务恢复失败：" + processResult3.CombinedOutput);
				}
				ScheduledTaskInspection scheduledTaskInspection2 = await _task.InspectAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				if (scheduledTaskInspection2.QueryFailed || !scheduledTaskInspection2.Exists || !scheduledTaskInspection2.MatchesExpectedDefinition || scheduledTaskInspection2.EffectiveEnabled != backup.TaskWasEnabled)
				{
					errors.Add("任务恢复后的精确定义复核失败：" + string.Join("；", from x in scheduledTaskInspection2.Issues.Append(scheduledTaskInspection2.QueryError)
						where !string.IsNullOrWhiteSpace(x)
						select x));
				}
				if (errors.Count > 0)
				{
					ProcessResult processResult4 = await _task.DeleteAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
					if (!processResult4.Success)
					{
						errors.Add("恢复校验失败后删除任务也失败：" + processResult4.CombinedOutput);
					}
				}
			}
		}
		catch (Exception ex7)
		{
			errors.Add("任务恢复失败：" + ex7.Message);
		}
		if (wasRunning && previousTaskXml != null && errors.Count == 0)
		{
			try
			{
				await StartAndValidateRestoredRuntimeAsync(backup.TaskWasEnabled, backup.RuntimeUser, backup.ExpectedRuntime ?? throw new InvalidDataException("缺少原运行态身份快照。"), ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex8)
			{
				errors.Add("原 Agent 恢复启动失败：" + ex8.Message);
			}
		}
		return (errors.Count == 0) ? (Success: true, Detail: "已恢复原程序文件、配置、计划任务与运行状态") : (Success: false, Detail: string.Join("；", errors) + "；回滚备份已保留：" + backup.RootPath);
	}

	private static string ScheduleSelfDelete()
	{
		if (!MoveFileEx(AppPaths.ControlExe, null, 4u))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安排控制程序在 Windows 重启后删除。");
		}
		MoveFileEx("C:\\Program Files\\P2PAgent", null, 4u);
		return AppPaths.ControlExe + " 已由 Windows 安排在下次重启时删除";
	}

	private static bool TryGetPendingControlSelfDelete(out bool pending, out string error)
	{
		pending = false;
		error = "";
		try
		{
			using RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager", writable: false);
			if (!(registryKey?.GetValue("PendingFileRenameOperations", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string[] array))
			{
				return true;
			}
			for (int i = 0; i < array.Length; i += 2)
			{
				string text = NormalizePendingRenamePath(array[i]);
				if (((i + 1 < array.Length) ? NormalizePendingRenamePath(array[i + 1]) : "").Length == 0 && text.Length > 0 && PathsEqual(text, AppPaths.ControlExe))
				{
					pending = true;
					return true;
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			error = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}

	private static string NormalizePendingRenamePath(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		string text = value.Trim().TrimEnd('\0');
		if (text.StartsWith("\\??\\", StringComparison.Ordinal) || text.StartsWith("\\\\?\\", StringComparison.Ordinal))
		{
			string text2 = text;
			text = text2.Substring(4, text2.Length - 4);
		}
		if (text.StartsWith('!'))
		{
			string text2 = text;
			text = text2.Substring(1, text2.Length - 1);
		}
		return text;
	}

	private static void TryDeleteRequired(string path)
	{
		if (File.Exists(path))
		{
			RuntimeSecurityService.RejectReparsePoint(path);
			File.Delete(path);
			if (File.Exists(path))
			{
				throw new IOException("文件删除后仍存在：" + path);
			}
		}
	}

	private static void TryDelete(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private static void TryDeleteEmptyDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
			{
				Directory.Delete(path);
			}
		}
		catch
		{
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
