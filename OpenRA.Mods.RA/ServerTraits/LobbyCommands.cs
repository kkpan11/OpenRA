#region Copyright & License Information
/*
 * Copyright 2007-2011 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made 
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Network;
using OpenRA.FileFormats;
using OpenRA.Server;
using S = OpenRA.Server.Server;

namespace OpenRA.Mods.RA.Server
{
	public class LobbyCommands : ServerTrait, IInterpretCommand, INotifyServerStart
	{
		public bool InterpretCommand(S server, Connection conn, Session.Client client, string cmd)
		{
			if (server.GameStarted)
			{
				server.SendChatTo(conn, "Cannot change state when game started. ({0})".F(cmd));
				return false;
			}
			else if (client.State == Session.ClientState.Ready && !(cmd == "ready" || cmd == "startgame"))
			{
				server.SendChatTo(conn, "Cannot change state when marked as ready.");
				return false;
			}
			
			var dict = new Dictionary<string, Func<string, bool>>
			{
				{ "ready",
					s =>
					{
						// if we're downloading, we can't ready up.
						if (client.State == Session.ClientState.NotReady)
							client.State = Session.ClientState.Ready;
						else if (client.State == Session.ClientState.Ready)
							client.State = Session.ClientState.NotReady;

						Log.Write("server", "Player @{0} is {1}",
							conn.socket.RemoteEndPoint, client.State);

						server.SyncLobbyInfo();
						
						if (server.conns.Count > 0 && server.conns.All(c => server.GetClient(c).State == Session.ClientState.Ready))
							InterpretCommand(server, conn, client, "startgame");
						
						return true;
					}},
				{ "startgame", 
					s => 
					{
						server.StartGame();
						return true;
					}},
				{ "lag",
					s =>
					{
						int lag;
						if (!int.TryParse(s, out lag)) { Log.Write("server", "Invalid order lag: {0}", s); return false; }

						Log.Write("server", "Order lag is now {0} frames.", lag);

						server.lobbyInfo.GlobalSettings.OrderLatency = lag;
						server.SyncLobbyInfo();
						return true;
					}},
				{ "slot",
					s =>
					{
						if (!server.lobbyInfo.Slots.ContainsKey(s))
						{
							Log.Write("server", "Invalid slot: {0}", s );
							return false;
						}
						var slot = server.lobbyInfo.Slots[s];

						if (slot.Closed || server.lobbyInfo.ClientInSlot(s) != null)
							return false;

						client.Slot = s;
						S.SyncClientToPlayerReference(client, server.Map.Players[s]);

						server.SyncLobbyInfo();
						return true;
					}},
				{ "spectate",
					s =>
					{
						client.Slot = null;
						client.SpawnPoint = 0;
						server.SyncLobbyInfo();
						return true;
					}},
				{ "slot_close",
					s =>
					{
						if (!server.lobbyInfo.Slots.ContainsKey(s))
						{
							Log.Write("server", "Invalid slot: {0}", s );
							return false;
						}

						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can alter slots" );
							return true;
						}

						// kick any player that's in the slot
						var occupant = server.lobbyInfo.ClientInSlot(s);
						if (occupant != null)
						{
							if (occupant.Bot != null)
								server.lobbyInfo.Clients.Remove(occupant);
							else
							{
								var occupantConn = server.conns.FirstOrDefault( c => c.PlayerIndex == occupant.Index );
								if (occupantConn != null)
								{
									server.SendOrderTo(occupantConn, "ServerError", "Your slot was closed by the host");
									server.DropClient(occupantConn);
								}
							}
						}

						server.lobbyInfo.Slots[s].Closed = true;
						server.SyncLobbyInfo();
						return true;
					}},
				{ "slot_open",
					s =>
					{
						if (!server.lobbyInfo.Slots.ContainsKey(s))
						{
							Log.Write("server", "Invalid slot: {0}", s );
							return false;
						}

						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can alter slots" );
							return true;
						}

						var slot = server.lobbyInfo.Slots[s];
						slot.Closed = false;

						// Slot may have a bot in it
						var occupant = server.lobbyInfo.ClientInSlot(s);
						if (occupant != null && occupant.Bot != null)
							server.lobbyInfo.Clients.Remove(occupant);

						server.SyncLobbyInfo();
						return true;
					}},
				{ "slot_bot",
					s =>
					{
						var parts = s.Split(' ');

						if (parts.Length < 2)
						{
							server.SendChatTo( conn, "Malformed slot_bot command" );
							return true;
						}

						if (!server.lobbyInfo.Slots.ContainsKey(parts[0]))
						{
							Log.Write("server", "Invalid slot: {0}", parts[0] );
							return false;
						}

						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can alter slots" );
							return true;
						}

						var botType = string.Join(" ", parts.Skip(1).ToArray() );
						var slot = server.lobbyInfo.Slots[parts[0]];
						slot.Closed = false;

						var bot = new Session.Client()
						{
							Index = server.ChooseFreePlayerIndex(),
							Name = botType,
							Bot = botType,
							Slot = parts[0],
							Country = "random",
							SpawnPoint = 0,
							Team = 0,
							State = Session.ClientState.NotReady
						};

						// pick a random color for the bot
						var hue = (byte)Game.CosmeticRandom.Next(255);
						var sat = (byte)Game.CosmeticRandom.Next(255);
						var lum = (byte)Game.CosmeticRandom.Next(51,255);
						bot.ColorRamp = new ColorRamp(hue, sat, lum, 10);

						S.SyncClientToPlayerReference(client, server.Map.Players[parts[0]]);
						server.lobbyInfo.Clients.Add(bot);
						server.SyncLobbyInfo();
						return true;
					}},
				{ "map",
					s =>
					{
						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can change the map" );
							return true;
						}
						server.lobbyInfo.GlobalSettings.Map = s;
						var oldSlots = server.lobbyInfo.Slots.Keys.ToArray();
						LoadMap(server);

						// Reassign players into new slots based on their old slots:
						//  - Observers remain as observers
						//  - Players who now lack a slot are made observers
						//  - Bots who now lack a slot are dropped
						var slots = server.lobbyInfo.Slots.Keys.ToArray();
						int i = 0;
						foreach (var os in oldSlots)
						{
							var c = server.lobbyInfo.ClientInSlot(os);
							if (c == null)
								continue;

							c.SpawnPoint = 0;
							c.State = Session.ClientState.NotReady;
							c.Slot = i < slots.Length ? slots[i++] : null;
							if (c.Slot != null)
								S.SyncClientToPlayerReference(c, server.Map.Players[c.Slot]);
							else if (c.Bot != null)
								server.lobbyInfo.Clients.Remove(c);
						}
						
						server.SyncLobbyInfo();
						return true;
					}},
				{ "lockteams",
					s =>
					{
						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can set that option" );
							return true;
						}
						
						bool.TryParse(s, out server.lobbyInfo.GlobalSettings.LockTeams);
						server.SyncLobbyInfo();
						return true;
					}},
				{ "allowcheats",
					s =>
					{
						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can set that option" );
							return true;
						}
						
						bool.TryParse(s, out server.lobbyInfo.GlobalSettings.AllowCheats);
						server.SyncLobbyInfo();
						return true;
					}},
				{ "kick",
					s => 
					{

						if (conn.PlayerIndex != 0)
						{
							server.SendChatTo( conn, "Only the host can kick players" );
							return true;
						}

						int clientID;
						int.TryParse( s, out clientID );

						var connToKick = server.conns.SingleOrDefault( c => server.GetClient(c) != null && server.GetClient(c).Index == clientID);
						if (connToKick == null) 
						{
							server.SendChatTo( conn, "Noone in that slot." );
							return true;
						}
						
						server.SendOrderTo(connToKick, "ServerError", "You have been kicked from the server");
						server.DropClient(connToKick);
						server.SyncLobbyInfo();
						return true;
					}},
			};
			
			var cmdName = cmd.Split(' ').First();
			var cmdValue = string.Join(" ", cmd.Split(' ').Skip(1).ToArray());

			Func<string,bool> a;
			if (!dict.TryGetValue(cmdName, out a))
				return false;
			
			return a(cmdValue);
		}
		
		public void ServerStarted(S server) { LoadMap(server); }
		static Session.Slot MakeSlotFromPlayerReference(PlayerReference pr)
		{
			if (!pr.Playable) return null;
			return new Session.Slot
			{
				PlayerReference = pr.Name,
				Closed = false,
				AllowBots = pr.AllowBots,
				LockRace = pr.LockRace,
				LockColor = pr.LockColor,
				LockTeam = pr.LockTeam,
				LockSpawn = pr.LockSpawn
			};
		}

		public static void LoadMap(S server)
		{
			server.Map = new Map(server.ModData.AvailableMaps[server.lobbyInfo.GlobalSettings.Map].Path);
			server.lobbyInfo.Slots = server.Map.Players
				.Select(p => MakeSlotFromPlayerReference(p.Value))
				.Where(s => s != null)
				.ToDictionary(s => s.PlayerReference, s => s);
		}
	}
}
