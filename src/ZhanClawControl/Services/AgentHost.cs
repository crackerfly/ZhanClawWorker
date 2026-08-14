#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZhanClawControl.Services;

public static class AgentHost
{
	public const string Switch = "--run-agent";

	private static readonly object LogLock = new object();

	public static bool IsHostMode(IEnumerable<string> args)
	{
		return args.Any((string a) => string.Equals(a, "--run-agent", StringComparison.OrdinalIgnoreCase));
	}

	public static async Task<int> RunAsync(CancellationToken ct = default(CancellationToken))
	{
		RuntimeSecurityService.MaintenanceStartAuthorization startAuthorization;
		try
		{
			startAuthorization = RuntimeSecurityService.EnforceMaintenanceStartBoundaryForCurrentUser();
		}
		catch
		{
			return 7;
		}
		try
		{
			Directory.CreateDirectory(AppPaths.LogDirectory);
		}
		catch
		{
		}
		RollLogIfNeeded();
		StreamWriter writer = null;
		try
		{
			writer = new StreamWriter(new FileStream(AppPaths.AgentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
			{
				AutoFlush = true
			};
		}
		catch
		{
		}
		if (!File.Exists(AppPaths.AgentExe))
		{
			Log("[host]", "agent executable not found: " + AppPaths.AgentExe);
			DisposeWriter();
			return 2;
		}
		if (!File.Exists(AppPaths.ConfigFile))
		{
			Log("[host]", "config not found: " + AppPaths.ConfigFile);
			DisposeWriter();
			return 3;
		}
		try
		{
			RuntimeSecurityService.ValidateRuntimeSecretsForCurrentUser();
			RuntimeSecurityService.ValidateRuntimeProvisioningStartBoundary();
			AgentConfigService.ValidateRuntimeBoundary(new AgentConfigService().Load());
			RuntimeSecurityService.ValidateSwarmKey(AppPaths.SwarmKeyFile);
			if (!startAuthorization.IsMaintenance)
			{
				await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				RuntimeSecurityService.RejectReparsePoint(AppPaths.AgentExe);
				using FileStream agent = new FileStream(AppPaths.AgentExe, FileMode.Open, FileAccess.Read, FileShare.Read);
				if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(agent, ct).ConfigureAwait(continueOnCapturedContext: false)), startAuthorization.AgentSha256, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Agent bytes no longer match the consumed maintenance permit.");
				}
				if (!startAuthorization.AllowTrustedRollbackPayload)
				{
					await RuntimeSecurityService.ValidateAgentPayloadAsync(AppPaths.AgentExe, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					RuntimeSecurityService.ValidateTrustedAgentPublisherForRollback(AppPaths.AgentExe);
				}
			}
		}
		catch (Exception ex)
		{
			Log("[host]", "runtime security validation failed: " + ex.GetType().Name + ": " + ex.Message);
			DisposeWriter();
			return 7;
		}
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = AppPaths.AgentExe,
			WorkingDirectory = "C:\\Program Files\\P2PAgent",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		processStartInfo.ArgumentList.Add("-config");
		processStartInfo.ArgumentList.Add(AppPaths.ConfigFile);
		using (Process process = new Process
		{
			StartInfo = processStartInfo
		})
		{
			process.OutputDataReceived += delegate(object _, DataReceivedEventArgs e)
			{
				Log("[agent]", e.Data);
			};
			process.ErrorDataReceived += delegate(object _, DataReceivedEventArgs e)
			{
				Log("[agent:err]", e.Data);
			};
			Log("[host]", $"starting \"{AppPaths.AgentExe}\" -config \"{AppPaths.ConfigFile}\"");
			try
			{
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
			}
			catch (Exception ex2)
			{
				Log("[host]", "failed to start agent: " + ex2.GetType().Name + ": " + ex2.Message);
				DisposeWriter();
				return 4;
			}
			try
			{
				await process.WaitForExitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				process.WaitForExit();
				int exitCode = process.ExitCode;
				int result = ((exitCode == 0) ? 6 : exitCode);
				Log("[host]", (exitCode == 0) ? "agent exited unexpectedly with code 0; host maps it to code 6 for restart policy" : $"agent exited with code {exitCode}");
				return result;
			}
			catch (OperationCanceledException)
			{
				Log("[host]", "host cancelled, terminating agent");
				TryKill(process);
				return 5;
			}
			finally
			{
				try
				{
					if (process.HasExited)
					{
						process.WaitForExit();
					}
					else
					{
						process.CancelOutputRead();
						process.CancelErrorRead();
					}
				}
				catch
				{
				}
				DisposeWriter();
			}
		}
		void DisposeWriter()
		{
			lock (LogLock)
			{
				StreamWriter streamWriter = writer;
				writer = null;
				streamWriter?.Dispose();
			}
		}
		void Log(string channel, string? line)
		{
			if (line == null || writer == null)
			{
				return;
			}
			try
			{
				string value = (HasLeadingTimestamp(line) ? (channel + " " + line) : $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {channel} {line}");
				lock (LogLock)
				{
					writer.WriteLine(value);
				}
			}
			catch
			{
			}
		}
	}

	private static bool HasLeadingTimestamp(string line)
	{
		if (line.Length >= 19 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]) && char.IsDigit(line[3]) && line[4] == '/' && line[7] == '/' && line[10] == ' ' && line[13] == ':')
		{
			return line[16] == ':';
		}
		return false;
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(5000);
			}
		}
		catch
		{
		}
	}

	private static void RollLogIfNeeded()
	{
		try
		{
			if (File.Exists(AppPaths.AgentLogFile) && new FileInfo(AppPaths.AgentLogFile).Length > 8388608)
			{
				if (File.Exists(AppPaths.AgentLogRollFile))
				{
					File.Delete(AppPaths.AgentLogRollFile);
				}
				File.Move(AppPaths.AgentLogFile, AppPaths.AgentLogRollFile);
			}
		}
		catch
		{
		}
	}
}
