#nullable disable warnings
using System;

namespace ZhanClawControl.ViewModels;

public sealed class AuthorizationChangedEventArgs : EventArgs
{
	public bool RuntimeVerified { get; }

	public AuthorizationChangedEventArgs(bool runtimeVerified)
	{
		RuntimeVerified = runtimeVerified;
	}
}
