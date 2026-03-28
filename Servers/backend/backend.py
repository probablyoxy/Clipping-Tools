import asyncio
import websockets
import json
import logging
import os

logging.getLogger("websockets").setLevel(logging.CRITICAL)

VERIFIED_UUIDS_FILE = "verified_uuids.json"
if os.path.exists(VERIFIED_UUIDS_FILE):
    with open(VERIFIED_UUIDS_FILE, "r") as f:
        verified_uuids = json.load(f)
else:
    verified_uuids = {}

def save_verified_uuids():
    with open(VERIFIED_UUIDS_FILE, "w") as f:
        json.dump(verified_uuids, f)

CONFIG_FILE = "config.json"
if os.path.exists(CONFIG_FILE):
    with open(CONFIG_FILE, "r") as f:
        config = json.load(f)
        ALLOWED_BOT_IDS = config.get("ALLOWED_BOT_IDS", [])
        PREFERRED_BOT_ID = config.get("PREFERRED_BOT_ID", "")
else:
    print("[Warning] config.json not found! Bots will not be able to authenticate.")
    ALLOWED_BOT_IDS = []
    PREFERRED_BOT_ID = ""

active_connections = {} 
unverified_connections = {}
pending_dm_requests = {}

user_approved_lists = {}

vc_map = {} 
user_vc_timestamps = {}

active_bots = {}
bot_guild_stats = {}

async def assign_guilds_to_bots():
    guild_candidates = {}
    for ws, data in bot_guild_stats.items():
        bot_id = data["bot_id"]
        for g_id, g_stats in data["guilds"].items():
            if g_id not in guild_candidates: guild_candidates[g_id] = []
            guild_candidates[g_id].append({
                "ws": ws,
                "has_admin": g_stats["has_admin"],
                "vc_count": g_stats["vc_count"],
                "bot_id": bot_id
            })

    bot_assignments = {ws: [] for ws in bot_guild_stats.keys()}

    for g_id, candidates in guild_candidates.items():
        def sort_key(c):
            is_pref = 1 if c["bot_id"] == PREFERRED_BOT_ID else 0
            has_adm = 1 if c["has_admin"] else 0
            return (has_adm, c["vc_count"], is_pref)

        candidates.sort(key=sort_key, reverse=True)
        winner = candidates[0]
        bot_assignments[winner["ws"]].append(g_id)

    for ws, g_ids in bot_assignments.items():
        try:
            asyncio.create_task(ws.send(json.dumps({
                "action": "assign_guilds",
                "guild_ids": g_ids
            })))
        except: pass

async def try_next_dm_bot(user_id):
    req = pending_dm_requests.get(user_id)
    if not req: return
    
    if not req["bots_to_try"]:
        try: await req["desktop_ws"].send(json.dumps({"action": "dm_verification_failed"}))
        except: pass
        if user_id in pending_dm_requests:
            del pending_dm_requests[user_id]
        return
        
    next_bot_ws = req["bots_to_try"].pop(0)
    try:
        await next_bot_ws.send(json.dumps({
            "action": "try_dm_link",
            "user_id": user_id,
            "app_uuid": req["app_uuid"]
        }))
    except:
        await try_next_dm_bot(user_id)

def broadcast_vc_updates():
    for client_id, ws in list(active_connections.items()):
        client_vc_data = vc_map.get(client_id)
        if not client_vc_data:
            payload_map = {}
        else:
            client_vc_id = client_vc_data["id"]
            payload_map = {}
            for u, c in vc_map.items():
                if c["id"] == client_vc_id:
                    i_have_them = u in user_approved_lists.get(client_id, [])
                    they_have_me = client_id in user_approved_lists.get(u, [])
                    
                    rel = "none"
                    if i_have_them and they_have_me: rel = "mutual"
                    elif i_have_them: rel = "outgoing"
                    elif they_have_me: rel = "incoming"

                    payload_map[u] = {
                        "id": c["id"],
                        "name": c["name"],
                        "user_name": c.get("user_name", "Unknown User"),
                        "is_connected": u in active_connections,
                        "relationship": rel
                    }
        
        try:
            asyncio.create_task(ws.send(json.dumps({
                "action": "client_vc_update",
                "vc_map": payload_map
            })))
        except: pass

async def handle_client(websocket):
    user_id = None
    is_bot = False

    try:
        async for message in websocket:
            data = json.loads(message)
            action = data.get("action")

            # ==========================================
            # DESKTOP APP MESSAGES
            # ==========================================
            if action == "identify":
                user_id = data.get("user_id")
                app_uuid = data.get("app_uuid")
                approved_users = data.get("approved_users", [])
                
                if not user_id or not app_uuid:
                    continue

                if verified_uuids.get(user_id) == app_uuid:
                    active_connections[user_id] = websocket
                    user_approved_lists[user_id] = approved_users
                    print(f"[Desktop] User {user_id} connected. Friends list size: {len(approved_users)}")
                    broadcast_vc_updates()
                else:
                    unverified_connections[user_id] = {"ws": websocket, "approved_users": approved_users}
                    print(f"[Desktop] User {user_id} connected with unverified UUID. Requesting bot link.")
                    
                    pref_ws = None
                    other_ws = []
                    for b_ws, b_id in active_bots.items():
                        if b_id == PREFERRED_BOT_ID: pref_ws = b_ws
                        else: other_ws.append(b_ws)
                    
                    bots_to_try = ([pref_ws] if pref_ws else []) + other_ws
                    pending_dm_requests[user_id] = {
                        "app_uuid": app_uuid,
                        "bots_to_try": bots_to_try,
                        "desktop_ws": websocket
                    }
                    asyncio.create_task(try_next_dm_bot(user_id))

            elif action == "update_users":
                if user_id:
                    user_approved_lists[user_id] = data.get("approved_users", [])
                    print(f"[Desktop] User {user_id} updated their friend list.")
                    broadcast_vc_updates()

            elif action == "resolve_ids":
                for bot_ws in list(active_bots.keys()):
                    try: await bot_ws.send(message)
                    except: pass

            elif action == "get_all_users":
                for bot_ws in list(active_bots.keys()):
                    try: await bot_ws.send(message)
                    except: pass

            elif action == "trigger":
                if not user_id: continue
                app_uuid = data.get("app_uuid")

                if verified_uuids.get(user_id) != app_uuid:
                    print(f"[Security] Blocked unverified trigger from {user_id}")
                    continue

                sender_data = vc_map.get(user_id)
                if not sender_data:
                    print(f"[Desktop] User {user_id} clipped, but a bot doesn't see them in a VC.")
                    continue

                sender_channel = sender_data.get("id")
                sender_name = sender_data.get("user_name", "Unknown User")
                print(f"[Desktop] User {user_id} ({sender_name}) triggered a clip in VC {sender_channel}.")

                for target_user, c_data in vc_map.items():
                    if c_data.get("id") == sender_channel and target_user != user_id:

                        if target_user in active_connections:
                            target_ws = active_connections[target_user]
                            payload = {
                                "action": "sync_clip",
                                "sender_id": user_id
                            }
                            await target_ws.send(json.dumps(payload))
                            target_name = c_data.get("user_name", "Unknown User")
                            print(f"[Router] Sent clip signal from {user_id} ({sender_name}) -> {target_user} ({target_name})")


            # ==========================================
            # DISCORD BOT MESSAGES (THE EYES)
            # ==========================================
            elif action == "bot_verify_uuid":
                if websocket not in active_bots: continue
                target_user = data.get("user_id")
                app_uuid = data.get("app_uuid")
                
                if target_user and app_uuid:
                    verified_uuids[target_user] = app_uuid
                    save_verified_uuids()
                    print(f"[Bot] Verified UUID for user {target_user}")
                    
                    if target_user in unverified_connections:
                        conn_data = unverified_connections.pop(target_user)
                        active_connections[target_user] = conn_data["ws"]
                        user_approved_lists[target_user] = conn_data["approved_users"]
                        print(f"[Desktop] Moved {target_user} to active connections.")
                        broadcast_vc_updates()

            elif action == "bot_identify":
                bot_id = data.get("bot_id")
                if bot_id in ALLOWED_BOT_IDS:
                    is_bot = True
                    active_bots[websocket] = bot_id
                    print(f"[Bot] A Discord Bot authenticated with ID: {bot_id}")
                else:
                    print(f"[Security] Rejected bot connection with invalid ID: {bot_id}")

            elif action == "bot_guild_sync":
                if websocket not in active_bots: continue
                bot_id = data.get("bot_id")
                guilds = data.get("guilds", {})
                bot_guild_stats[websocket] = {"bot_id": bot_id, "guilds": guilds}
                print(f"[Router] Synced {len(guilds)} guilds for bot {bot_id}")
                await assign_guilds_to_bots()

            elif action == "dm_result":
                if websocket not in active_bots: continue
                target_user = data.get("user_id")
                success = data.get("success")
                if success:
                    if target_user in pending_dm_requests:
                        del pending_dm_requests[target_user]
                else:
                    asyncio.create_task(try_next_dm_bot(target_user))

            elif action == "resolved_ids":
                if websocket not in active_bots: continue
                target_client = data.get("client_id")
                if target_client in active_connections:
                    try: await active_connections[target_client].send(message)
                    except: pass
					
            elif action == "all_users_list":
                if websocket not in active_bots: continue
                target_client = data.get("client_id")
                if target_client in active_connections:
                    try: await active_connections[target_client].send(message)
                    except: pass

            elif action == "vc_update":
                if websocket not in active_bots: continue
                target_user = data.get("user_id")
                channel_id = data.get("channel_id")
                channel_name = data.get("channel_name")
                user_name = data.get("user_name", "Unknown User")
                event_ts = data.get("timestamp", 0)
                
                last_ts = user_vc_timestamps.get(target_user, 0)
                
                if event_ts < last_ts:
                    continue
                    
                user_vc_timestamps[target_user] = event_ts
                
                if channel_id:
                    current = vc_map.get(target_user)
                    if current and current.get("id") == channel_id:
                        continue
                        
                    vc_map[target_user] = {"id": channel_id, "name": channel_name, "user_name": user_name}
                    print(f"[Bot Update] {target_user} ({user_name}) joined VC {channel_name}.")
                else:
                    if target_user in vc_map:
                        del vc_map[target_user]
                        print(f"[Bot Update] {target_user} left VC.")
                    else:
                        continue
                
                broadcast_vc_updates()

    except websockets.exceptions.ConnectionClosed:
        pass
    except Exception as e:
        print(f"[Error] {e}")
    finally:
            if user_id:
                if user_id in active_connections and active_connections[user_id] == websocket:
                    del active_connections[user_id]
                    del user_approved_lists[user_id]
                    print(f"[Desktop] User {user_id} disconnected.")
                    broadcast_vc_updates()
                            
                if user_id in unverified_connections and unverified_connections[user_id]["ws"] == websocket:
                    del unverified_connections[user_id]
                    print(f"[Desktop] Unverified User {user_id} disconnected.")
            elif is_bot:
                if websocket in active_bots:
                    del active_bots[websocket]
                if websocket in bot_guild_stats:
                    del bot_guild_stats[websocket]
                    asyncio.create_task(assign_guilds_to_bots())
                print("[Bot] A Discord Bot disconnected.")

async def main():
    print("Starting Clipping Tools Central Router on port 8765...")
    async with websockets.serve(handle_client, "0.0.0.0", 8765, max_size=None):
        await asyncio.Future()

if __name__ == "__main__":
    asyncio.run(main())