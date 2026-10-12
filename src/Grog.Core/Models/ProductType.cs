// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public enum ProductType { Game = 0, Movie = 1, Mod = 2, Pack = 3, Dlc = 4 }
