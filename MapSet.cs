using System;
using System.Collections.Generic;

namespace DutchSkies
{
	public class MapSet
	{
		public string id;
		public Dictionary<string, OSMMap> maps;
		public string default_map;

		public MapSet()
		{
			maps = new Dictionary<string, OSMMap>();
			default_map = "";
		}

		public void Add(string id, OSMMap map)
        {
			maps[id] = map;
			if (maps.Count == 1)
				default_map = id;
		}
	}
}