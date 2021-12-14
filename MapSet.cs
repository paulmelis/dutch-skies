using System;
using System.Collections.Generic;
using StereoKit;

namespace DutchSkies
{
	public class MapSet
	{
		public string id;
		public Dictionary<string, OSMMap> maps;
		public string default_map;
		public Vec4 query_extent;

		public MapSet(string id)
		{
			this.id = id;
			maps = new Dictionary<string, OSMMap>();
			default_map = "";
			query_extent = new Vec4();
		}

		public void Add(string map_id, OSMMap map)
		{
			Log.Info($"MapSet.Add '{map_id}'");
			maps[map_id] = map;
			if (maps.Count == 1)
            {
				Log.Info($"Map set '{id}: default map set to '{map_id}'");
				default_map = map_id;
				Log.Info($"default_map = '{default_map}'");
			}				
		}
	}
}