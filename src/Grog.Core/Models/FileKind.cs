// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public enum FileKind { Installer = 0, Patch = 1, LanguagePack = 2, Extra = 3, DlcInstaller = 4 }
