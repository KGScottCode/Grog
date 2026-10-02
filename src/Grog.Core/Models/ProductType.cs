// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>Product kinds in a GOG library, derived by Grog because GOG's data blurs them: Game = standalone
/// with installers; Movie = video; Mod = game-typed product in GOG's mod tag list; Pack = bundle whose files
/// live in its child games (returns "[]"); Dlc = add-on under a base game (returns "[]").</summary>
public enum ProductType { Game = 0, Movie = 1, Mod = 2, Pack = 3, Dlc = 4 }
