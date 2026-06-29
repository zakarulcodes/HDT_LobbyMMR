using ScryDotNet;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

namespace HDT_LobbyMMR
{
    /// <summary>
    /// Thin wrapper over UnitySpy (untapped-scry-dotnet) used to read Hearthstone
    /// memory. Mirrors the approach used by HDT_BGrank: the in-game leaderboard
    /// player names are only available through memory, not through HDT's public API.
    /// </summary>
    public class Mirror
    {
        // Unity engine version Hearthstone is built with. Stable across most HS
        // patches; only needs updating if Blizzard upgrades the Unity engine.
        private const string UnityVersion = "2021.3.25.61228";

        private MonoImage _root;
        private string _myName;

        public MonoImage Root
        {
            get
            {
                if (_root != null) { return _root; }

                using Process proc = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
                if (proc == null) { return null; }
                using MonoScry view = new MonoScry(Scry.connect(proc.Id));
                _root = view.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, UnityVersion);
                return _root;
            }
        }

        public string MyName
        {
            get
            {
                if (_myName != null) { return _myName; }

                dynamic presenceMgr = Root?["BnetPresenceMgr"]?["s_instance"];
                _myName = presenceMgr?["m_myPlayer"]?["m_account"]?["m_battleTag"]?["m_name"];
                return _myName;
            }
        }

        public void Clean()
        {
            _root?.Dispose();
            _root = null;
            _myName = null;
        }
    }
}
