using CitizenFX.Core.Native;
using JetBrains.Annotations;
using NFive.Login.Server.Helpers;
using NFive.Login.Server.Models;
using NFive.Login.Server.Storage;
using NFive.Login.Shared;
using NFive.Login.Shared.Responses;
using NFive.SDK.Core.Diagnostics;
using NFive.SDK.Server.Controllers;
using NFive.SDK.Server.Events;
using NFive.SDK.Server.Rcon;
using NFive.SDK.Server.Rpc;
using NFive.SDK.Server.Wrappers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NFive.Login.Server
{
	[PublicAPI]
	public class LoginController : ConfigurableController<Configuration>
	{
		private BCryptHelper bcrypt;
		private SessionManager sessions;
		private Dictionary<int, int> loginAttempts = new Dictionary<int, int>();
		private List<Account> loggedInAccounts = new List<Account>();

		public LoginController(ILogger logger, IEventManager events, IRpcHandler rpc, IRconManager rcon, Configuration configuration) : base(logger, events, rpc, rcon, configuration)
		{
			this.bcrypt = new BCryptHelper(this.Configuration.GlobalSalt, this.Configuration.BcryptCost);

			// Send configuration when requested
			this.Rpc.Event(LoginEvents.Configuration).On(e => e.Reply(new PublicConfiguration(this.Configuration)));

			this.Rpc.Event(LoginEvents.Register).On<Credentials>(OnRegistrationRequested);
			this.Rpc.Event(LoginEvents.Login).On<Credentials>(OnLoginRequested);
			this.Rpc.Event(LoginEvents.AuthenticationStarted).On(OnAuthenticationStarted);

			this.Events.OnRequest(LoginEvents.GetCurrentAccounts, () => this.loggedInAccounts);

			// Listen for NFive session events
			this.sessions = new SessionManager(this.Events, this.Rpc);
			this.sessions.ClientDisconnected += OnClientDisconnected;
		}

		private void OnClientDisconnected(object sender, ClientSessionEventArgs e)
		{
			if (this.loginAttempts.ContainsKey(e.Client.Handle)) this.loginAttempts.Remove(e.Client.Handle);

			this.loggedInAccounts.RemoveAll(a => a.UserId == e.Session.UserId);
		}

		private void OnAuthenticationStarted(IRpcEvent e)
		{
			this.Logger.Debug($"{e.User.Name} ({e.Client.Handle}) has started the authentication process!");

			this.loginAttempts.Add(e.Client.Handle, 0);
		}

		private async void OnLoginRequested(IRpcEvent e, Credentials credentials)
		{
			using (var context = new StorageContext())
			using (var transaction = context.Database.BeginTransaction())
			{
				try
				{
					var account = context.Accounts.FirstOrDefault(a => a.Email == credentials.Email && a.Deleted == null);

					if (account == null || !this.bcrypt.ValidatePassword(credentials.Password, account.Password))
					{
						e.Reply(LoginResponse.Invalid);

						this.loginAttempts[e.Client.Handle]++;

						if (this.Configuration.LoginAttempts <= 0 || this.loginAttempts[e.Client.Handle] < this.Configuration.LoginAttempts) return;

						this.Logger.Debug($"Kicking {e.User.Name} for exceeding the maximum allowed login attempts.");

						API.DropPlayer(e.Client.Handle.ToString(), "You have exceeded the maximum allowed login attempts!"); // TODO: Drop with SessionManager
					}
					else
					{
						account.LastLogin = DateTime.UtcNow;
						account.Password = this.bcrypt.UpdateHash(credentials.Password, account.Password);

						await context.SaveChangesAsync();
						transaction.Commit();

						this.loggedInAccounts.Add(account);

						this.Events.Raise(LoginEvents.LoggedIn, e.Client, account);
						this.Logger.Debug($"{e.User.Name} has just logged in ({credentials.Email})");

						e.Reply(LoginResponse.Valid);
					}
				}
				catch (Exception ex)
				{
					this.Logger.Error(ex);

					transaction.Rollback();

					e.Reply(LoginResponse.Error);
				}
			}
		}

		private async void OnRegistrationRequested(IRpcEvent e, Credentials credentials)
		{
			using (var context = new StorageContext())
			using (var transaction = context.Database.BeginTransaction())
			{
				try
				{
					if (this.Configuration.MaxAccountsPerUser != 0 && context.Accounts.Count(a => a.UserId == e.User.Id && a.Deleted == null) >= this.Configuration.MaxAccountsPerUser)
					{
						e.Reply(RegisterResponse.AccountLimitReached);
						return;
					}

					if (context.Accounts.Any(a => a.Email == credentials.Email && a.Deleted == null))
					{
						e.Reply(RegisterResponse.EmailExists);
						return;
					}

					var account = new Account
					{
						Email = credentials.Email,
						Password = this.bcrypt.HashPassword(credentials.Password),
						LastLogin = DateTime.UtcNow,
						UserId = e.User.Id
					};

					context.Accounts.Add(account);
					await context.SaveChangesAsync();
					transaction.Commit();

					this.Events.Raise(LoginEvents.Registered, e.Client, account);
					this.Logger.Debug($"{e.User.Name} has registered a new account ({credentials.Email})");

					this.loggedInAccounts.Add(account);

					this.Events.Raise(LoginEvents.LoggedIn, e.Client, account);
					this.Logger.Debug($"{e.User.Name} has just logged in ({credentials.Email})");

					e.Reply(RegisterResponse.Created);
				}
				catch (Exception ex)
				{
					this.Logger.Error(ex);

					transaction.Rollback();

					e.Reply(RegisterResponse.Error);
				}
			}
		}

		public override void Reload(Configuration configuration)
		{
			// Update local configuration
			base.Reload(configuration);

			// Update BCrypt settings
			this.bcrypt = new BCryptHelper(this.Configuration.GlobalSalt, this.Configuration.BcryptCost);

			// Send out new configuration
			this.Rpc.Event(LoginEvents.Configuration).Trigger(new PublicConfiguration(this.Configuration));
		}
	}
}
