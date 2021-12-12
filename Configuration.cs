using System.Collections.Generic;
using Windows.Storage;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    public class Configuration
    {
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

        public static void StoreConfiguration(string section, string id, JSONNode data)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

            string sdata = data.ToString();
            ApplicationDataContainer type_container;

            if (localSettings.Containers.ContainsKey(section))
                type_container = localSettings.Containers[section];
            else
                type_container = localSettings.CreateContainer(section, ApplicationDataCreateDisposition.Always);

            type_container.Values[id] = sdata;
        }

        public static void ListConfigurations(string section, ref List<string> items)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

            if (!localSettings.Containers.ContainsKey(section))
                return;

            ApplicationDataContainer type_container = localSettings.Containers[section];

            items.Clear();
            foreach (string id in type_container.Values.Keys)
                items.Add(id);
            items.Sort();
        }

        public static bool LoadConfiguration(string section, string id, out JSONNode node)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

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

        public static void DeleteSectionConfigurations(string section)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            localSettings.DeleteContainer(section);
        }

        public static void DeleteConfiguration(string section, string id)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

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