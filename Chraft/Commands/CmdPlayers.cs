﻿#region C#raft License
// This file is part of C#raft. Copyright C#raft Team 
// 
// C#raft is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
#endregion
using Chraft.Net;
using Chraft.Plugins;

namespace Chraft.Commands
{
    internal class CmdPlayers : IClientCommand
    {
       public ClientCommandHandler ClientCommandHandler { get; set; }

        public void Use(Client client, string commandName, string[] tokens)
        {
            client.SendMessage("Online Players: " + client.Owner.Server.Clients.Count);
            foreach (Client c in client.Owner.Server.GetAuthenticatedClients())
                client.SendMessage(c.Owner.EntityId + " : " + c.Owner.DisplayName);
        }

        public void Help(Client client)
        {
            client.SendMessage("/players - Shows a list of online players.");
        }

        public string Name
        {
            get { return "players"; }
        }

        public string Shortcut
        {
            get { return "who"; }
        }

        public CommandType Type
        {
            get { return CommandType.Other; }
        }

        public string Permission
        {
            get { return "chraft.players"; }
        }

        public IPlugin Iplugin { get; set; }
    }
}
