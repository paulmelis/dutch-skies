using System.Collections.Generic;
using Windows.Storage;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{    
    public class ConfigurationStore
    {
        public enum ConfigType
        {
            MAP_SET, LANDMARK_SET, OBSERVER
        };

        /*
         * section: "map_sets", "landmark_sets", "observers"
         * 
         * "map_sets"
         *      "Netherlands" -> "{ "id": "...", [....] }"
         *      ...
         *       
         * "landmark_sets"
         *      "Park" -> "{ "id": "...", [....] }"
         *      ...
         *    
         * "observers"
         *      "Home" -> "{ "id": "...", ...}"     
         * 
         */

        protected static string type2section(ConfigType type)
        {
            if (type == ConfigType.MAP_SET)
                return "map_sets";
            else if (type == ConfigType.LANDMARK_SET)
                return "landmark_sets";
            else
                return "observers";
        }

        public static void Store(ConfigType type, string id, JSONNode data)
        {            
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            Log.Info($"Storing configuration section '{section}', id '{id}'");

            ApplicationDataContainer type_container;

            if (localSettings.Containers.ContainsKey(section))
                type_container = localSettings.Containers[section];
            else
                type_container = localSettings.CreateContainer(section, ApplicationDataCreateDisposition.Always);

            if (type_container.Values.ContainsKey(id))
                Log.Info($"Overwriting configuration '{id}' (section '{section}')");

            type_container.Values[id] = data.ToString();
        }

        public static void Delete(ConfigType type, string id)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            if (!localSettings.Containers.ContainsKey(section))
            {
                Log.Err($"No configurations of type '{section}' stored (id '{id}')");
                return;
            }

            ApplicationDataContainer type_container = localSettings.Containers[section];
            type_container.Values.Remove(id);
        }
        public static void DeleteAllOfType(ConfigType type)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            localSettings.DeleteContainer(section);
        }

        public static void List(ConfigType type, ref List<string> items)
        {
            items.Clear();

            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            if (!localSettings.Containers.ContainsKey(section))
                return;
            
            ApplicationDataContainer type_container = localSettings.Containers[section];
            
            foreach (string id in type_container.Values.Keys)
                items.Add(id);
            items.Sort();
        }

        public static JSONNode Load(ConfigType type, string id)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            if (!localSettings.Containers.ContainsKey(section))
            {
                Log.Err($"No configurations of type '{section}' stored (id '{id}')");                
                return null;
            }

            ApplicationDataContainer type_container = localSettings.Containers[section];

            if (!type_container.Values.ContainsKey(id))
            {
                Log.Err($"No configuration '{id}' found (type '{section}')");
                return null;
            }

            string sdata = type_container.Values[id] as string;
            // XXX handle error
            return JSONNode.Parse(sdata);
        }
   }
}