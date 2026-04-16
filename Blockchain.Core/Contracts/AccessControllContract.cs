using System;
using System.Text.Json;

namespace Blockchain.Core.Contracts
{
    public class AccessControlContract : ISmartContract
    {
        public string Name => "AccessControl (RBAC)";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                if (!data.Contains("\"Type\":")) return true;

                Console.WriteLine($"\n[SmartContract Debug] Пришли данные: {data}");

                var evt = JsonSerializer.Deserialize<ContractTaskEvent>(data);
                if (evt == null || string.IsNullOrEmpty(evt.ProjectId))
                {
                    Console.WriteLine("[SmartContract Debug] ❌ Отказ: Не удалось распарсить JSON или ProjectId пустой.");
                    return false;
                }

                Console.WriteLine($"[SmartContract Debug] Запрашиваем роль для юзера '{evt.User}' в проекте '{evt.ProjectId}'...");
                string senderRole = db.GetUserRole(evt.ProjectId, evt.User);
                Console.WriteLine($"[SmartContract Debug] База данных вернула роль: '{senderRole}'");

                if (evt.Type == "AssignRole")
                {
                    if (string.IsNullOrEmpty(evt.TargetUser)) return false;

                    if (db.IsProjectExists(evt.ProjectId))
                    {
                        if (senderRole != "Owner" && senderRole != "Manager")
                        {
                            Console.WriteLine($"[SmartContract] RBAC Denied: {evt.User} is not an Admin in {evt.ProjectId}");
                            return false;
                        }

                        if (senderRole == "Manager" && evt.Role == "Owner")
                        {
                            Console.WriteLine($"[SmartContract] RBAC Denied: Managers cannot create Owners.");
                            return false;
                        }
                    }
                }

                if (evt.Type == "Move" || evt.Type == "Create")
                {
                    if (db.IsProjectExists(evt.ProjectId) && senderRole == "None")
                    {
                        Console.WriteLine($"[SmartContract] RBAC Denied: {evt.User} has no access to {evt.ProjectId}");
                        return false;
                    }
                }

                Console.WriteLine("[SmartContract Debug] ✅ Контракт успешно пройден!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartContract FATAL ERROR] Произошла скрытая ошибка кода: {ex.Message}");
                return false;
            }
        }
    }
}