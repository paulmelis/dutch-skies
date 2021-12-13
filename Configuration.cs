using System.Collections.Generic;
using Windows.Storage;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{    
    public class Configuration
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

        public static void StoreConfiguration(ConfigType type, string id, JSONNode data)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            ApplicationDataContainer type_container;

            if (localSettings.Containers.ContainsKey(section))
                type_container = localSettings.Containers[section];
            else
                type_container = localSettings.CreateContainer(section, ApplicationDataCreateDisposition.Always);

            type_container.Values[id] = data.ToString();         
        }

        public static void ListConfigurations(ConfigType type, ref List<string> items)
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

        public static bool LoadConfiguration(ConfigType type, string id, out JSONNode node)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            if (!localSettings.Containers.ContainsKey(section))
            {
                Log.Err($"No configurations of type '{section}' stored (id '{id}')");
                node = null;
                return false;
            }

            ApplicationDataContainer type_container = localSettings.Containers[section];

            if (!type_container.Values.ContainsKey(id))
            {
                Log.Err($"No configuration '{id}' found (type '{section}')");
                node = null;
                return false;
            }

            string sdata = type_container.Values[id] as string;
            node = JSONNode.Parse(sdata);

            return true;
        }

        public static void DeleteConfigurationsOfType(ConfigType type)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            localSettings.DeleteContainer(section);
        }

        public static void DeleteConfiguration(ConfigType type, string id)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string section = type2section(type);

            if (!localSettings.Containers.ContainsKey(section))
            {
                Log.Err($"No configurations of type '{section}' stored (id '{id}')");
                return;
            }

            ApplicationDataContainer type_container = localSettings.Containers[section];
            type_container.DeleteContainer(id);
        }
    }
}