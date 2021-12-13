using System;
using StereoKit;
using SimpleJSON;

namespace DutchSkies
{
	public class TileServerConfiguration
	{
		public string id;
		public string type;
		public string[] urls;

		public static TileServerConfiguration FromJSON(JSONNode spec)
        {
			TileServerConfiguration tsc = new TileServerConfiguration(spec["id"], spec["type"]);

			JSONArray jurls = spec["urls"].AsArray;
			string[] urls = new string[jurls.Count];
			for (int i = 0; i < jurls.Count; i++)
				urls[i] = jurls[i];
			tsc.urls = urls;

			return tsc;
		}

		public TileServerConfiguration(string id, string type)
		{
			this.id = id;
			this.type = type;
			if (type != "osm")
				Log.Warn($"Unknown tile server type '{type}'");
		}

		/*
		public string ToJSON()
        {
			JSONNode root = new JSONObject();

			root["id"] = id;
			root["type"] = type;
			root["urls"] = new JSONArray();
			foreach (string u in urls)
				root["urls"].Add(new JSONString(u));

			return root;
        }
		*/
	}
}