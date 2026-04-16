import asyncio
import websockets
import json
import logging
import os
import random
import string
import time

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

STATS_FILE = "stats.json"
if os.path.exists(STATS_FILE):
    with open(STATS_FILE, "r") as f:
        server_stats = json.load(f)
else:
    server_stats = {"clips_synced": 0, "clips_taken": 0}

def save_stats():
    with open(STATS_FILE, "w") as f:
        json.dump(server_stats, f)

active_connections = {}
unverified_connections = {}
pending_dm_requests = {}
pending_all_users_requests = {}

user_approved_lists = {}
user_versions = {}

vc_map = {} 
user_vc_timestamps = {}

user_last_clip_time = {}

pools = {}
user_pools = {}

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
                        "server_name": c.get("server_name", ""),
                        "is_connected": u in active_connections,
                        "relationship": rel
                    }
        
        try:
            asyncio.create_task(ws.send(json.dumps({
                "action": "client_vc_update",
                "vc_map": payload_map
            })))
        except: pass

def broadcast_pool_updates(pool_code):
    if pool_code not in pools: return
    pool_data = pools[pool_code]
    
    payload_members = {}
    for uid in pool_data["members"]:
        payload_members[uid] = {
            "is_connected": uid in active_connections
        }

    payload = {
        "action": "client_pool_update",
        "pool_code": pool_code,
        "name": pool_data["name"],
        "owner": pool_data["owner"],
        "is_open": pool_data["is_open"],
        "members": payload_members
    }
    
    for uid in pool_data["members"]:
        if uid in active_connections:
            try:
                asyncio.create_task(active_connections[uid].send(json.dumps(payload)))
            except: pass

def remove_user_from_pool(user_id):
    if user_id not in user_pools: return
    pool_code = user_pools[user_id]
    del user_pools[user_id]
    
    if pool_code in pools:
        if user_id in pools[pool_code]["members"]:
            pools[pool_code]["members"].remove(user_id)
            
        if not pools[pool_code]["members"]:
            del pools[pool_code]
            return
            
        if pools[pool_code]["owner"] == user_id:
            pools[pool_code]["owner"] = pools[pool_code]["members"][0]
            
        broadcast_pool_updates(pool_code)

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
                app_version = data.get("version", "≤v0.1.5")
                
                if not user_id or not app_uuid:
                    continue

                if verified_uuids.get(user_id) == app_uuid:
                    active_connections[user_id] = websocket
                    user_approved_lists[user_id] = approved_users
                    user_versions[user_id] = app_version
                    print(f"[Desktop] User {user_id} connected ({app_version}). Friends list size: {len(approved_users)}")
                    broadcast_vc_updates()
                else:
                    unverified_connections[user_id] = {"ws": websocket, "approved_users": approved_users, "version": app_version}
                    print(f"[Desktop] User {user_id} connected with unverified UUID ({app_version}). Requesting bot link.")
                    
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
                client_id = data.get("client_id")
                if client_id:
                    if not active_bots:
                        if client_id in active_connections:
                            try: await active_connections[client_id].send(json.dumps({"action": "all_users_list", "client_id": client_id, "users": {}}))
                            except: pass
                    else:
                        pending_all_users_requests[client_id] = {"bots_left": len(active_bots), "users": {}}
                        for bot_ws in list(active_bots.keys()):
                            try: await bot_ws.send(message)
                            except: pending_all_users_requests[client_id]["bots_left"] -= 1

            elif action == "trigger":
                if not user_id: continue
                app_uuid = data.get("app_uuid")

                if verified_uuids.get(user_id) != app_uuid:
                    print(f"[Security] Blocked unverified trigger from {user_id}")
                    continue

                current_time = time.time()
                if user_id in user_last_clip_time:
                    if current_time - user_last_clip_time[user_id] < 5.0:
                        print(f"[Rate Limit] Ignored clip trigger from {user_id} (Too fast).")
                        continue
                
                user_last_clip_time[user_id] = current_time

                target_users = set()
                sender_name = "Unknown User"

                sender_data = vc_map.get(user_id)
                if sender_data:
                    sender_channel = sender_data.get("id")
                    sender_name = sender_data.get("user_name", "Unknown User")
                    print(f"[Desktop] User {user_id} ({sender_name}) triggered a clip in VC {sender_channel}.")
                    for t_user, c_data in vc_map.items():
                        if c_data.get("id") == sender_channel and t_user != user_id:
                            target_users.add(t_user)

                pool_code = user_pools.get(user_id)
                if pool_code and pool_code in pools:
                    print(f"[Desktop] User {user_id} triggered a clip in Pool {pool_code}.")
                    for t_user in pools[pool_code]["members"]:
                        if t_user != user_id:
                            target_users.add(t_user)
                            
                if not sender_data and not pool_code:
                    print(f"[Desktop] User {user_id} clipped, but is not in a VC or a Pool.")
                    continue

                server_stats["clips_synced"] += 1
                server_stats["clips_taken"] += 1

                for target_user in target_users:
                    if target_user in active_connections:
                        target_ws = active_connections[target_user]
                        payload = {
                            "action": "sync_clip",
                            "sender_id": user_id
                        }
                        await target_ws.send(json.dumps(payload))
                        print(f"[Router] Sent clip signal from {user_id} -> {target_user}")
                        server_stats["clips_taken"] += 1
                
                save_stats()


            elif action == "create_pool":
                if not user_id: continue
                if user_id in user_pools: remove_user_from_pool(user_id)
                
                pool_name = data.get("name", "Unnamed Pool")
                pool_code = ''.join(random.choices(string.ascii_uppercase + string.digits, k=6))
                
                pools[pool_code] = {
                    "name": pool_name,
                    "owner": user_id,
                    "members": [user_id],
                    "banned": [],
                    "is_open": True
                }
                user_pools[user_id] = pool_code
                print(f"[Pools] User {user_id} created pool {pool_code}.")
                broadcast_pool_updates(pool_code)

            elif action == "join_pool":
                if not user_id: continue
                pool_code = data.get("pool_code", "").strip()
                
                if pool_code not in pools:
                    await websocket.send(json.dumps({"action": "pool_error", "message": "Pool not found."}))
                    continue
                if not pools[pool_code]["is_open"]:
                    await websocket.send(json.dumps({"action": "pool_error", "message": "This pool is currently closed."}))
                    continue
                if user_id in pools[pool_code]["banned"]:
                    await websocket.send(json.dumps({"action": "pool_error", "message": "You are banned from this pool."}))
                    continue
                    
                if user_id in user_pools: remove_user_from_pool(user_id)
                
                pools[pool_code]["members"].append(user_id)
                user_pools[user_id] = pool_code
                print(f"[Pools] User {user_id} joined pool {pool_code}.")
                broadcast_pool_updates(pool_code)

            elif action == "leave_pool":
                if user_id: remove_user_from_pool(user_id)

            elif action == "close_pool":
                if not user_id: continue
                pool_code = user_pools.get(user_id)
                if pool_code and pool_code in pools and pools[pool_code]["owner"] == user_id:
                    members_to_remove = list(pools[pool_code]["members"])
                    for member in members_to_remove:
                        if member in active_connections:
                            try: await active_connections[member].send(json.dumps({"action": "pool_closed"}))
                            except: pass
                        if member in user_pools: del user_pools[member]
                    del pools[pool_code]
                    print(f"[Pools] Pool {pool_code} closed by owner.")

            elif action == "toggle_pool":
                if not user_id: continue
                pool_code = user_pools.get(user_id)
                if pool_code and pool_code in pools and pools[pool_code]["owner"] == user_id:
                    pools[pool_code]["is_open"] = not pools[pool_code]["is_open"]
                    broadcast_pool_updates(pool_code)

            elif action == "pool_manage_user":
                if not user_id: continue
                pool_code = user_pools.get(user_id)
                target_uid = data.get("target_id")
                manage_action = data.get("manage_action")
                
                if pool_code and pool_code in pools and pools[pool_code]["owner"] == user_id:
                    if target_uid in pools[pool_code]["members"]:
                        if manage_action == "transfer":
                            pools[pool_code]["owner"] = target_uid
                            broadcast_pool_updates(pool_code)
                        elif manage_action == "kick":
                            if target_uid in active_connections:
                                try: await active_connections[target_uid].send(json.dumps({"action": "pool_kicked"}))
                                except: pass
                            remove_user_from_pool(target_uid)
                        elif manage_action == "ban":
                            pools[pool_code]["banned"].append(target_uid)
                            if target_uid in active_connections:
                                try: await active_connections[target_uid].send(json.dumps({"action": "pool_banned"}))
                                except: pass
                            remove_user_from_pool(target_uid)

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
                        user_versions[target_user] = conn_data.get("version", "≤v0.1.5")
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
                
                if target_client in pending_all_users_requests:
                    pending_all_users_requests[target_client]["users"].update(data.get("users", {}))
                    pending_all_users_requests[target_client]["bots_left"] -= 1
                    
                    if pending_all_users_requests[target_client]["bots_left"] <= 0:
                        combined_users = pending_all_users_requests[target_client]["users"]
                        if target_client in active_connections:
                            try: 
                                await active_connections[target_client].send(json.dumps({
                                    "action": "all_users_list", 
                                    "client_id": target_client, 
                                    "users": combined_users
                                }))
                            except: pass
                        del pending_all_users_requests[target_client]

            elif action == "vc_update":
                if websocket not in active_bots: continue
                target_user = data.get("user_id")
                channel_id = data.get("channel_id")
                channel_name = data.get("channel_name")
                user_name = data.get("user_name", "Unknown User")
                server_name = data.get("server_name", "")
                event_ts = data.get("timestamp", 0)
                
                last_ts = user_vc_timestamps.get(target_user, 0)
                
                if event_ts < last_ts:
                    continue
                    
                user_vc_timestamps[target_user] = event_ts
                
                if channel_id:
                    current = vc_map.get(target_user)
                    if current and current.get("id") == channel_id:
                        continue
                        
                    vc_map[target_user] = {"id": channel_id, "name": channel_name, "user_name": user_name, "server_name": server_name}
                    print(f"[Bot Update] {target_user} ({user_name}) joined VC {channel_name} in {server_name}.")
                else:
                    if target_user in vc_map:
                        del vc_map[target_user]
                        print(f"[Bot Update] {target_user} left VC.")
                    else:
                        continue
                
                broadcast_vc_updates()

            elif action == "bulk_vc_update":
                if websocket not in active_bots: continue
                updates = data.get("updates", [])
                changed = False
                
                for u in updates:
                    target_user = u.get("user_id")
                    channel_id = u.get("channel_id")
                    channel_name = u.get("channel_name")
                    user_name = u.get("user_name", "Unknown User")
                    server_name = u.get("server_name", "")
                    event_ts = u.get("timestamp", 0)
                    
                    last_ts = user_vc_timestamps.get(target_user, 0)
                    if event_ts < last_ts:
                        continue
                        
                    user_vc_timestamps[target_user] = event_ts
                    
                    if channel_id:
                        current = vc_map.get(target_user)
                        if current and current.get("id") == channel_id:
                            continue
                            
                        vc_map[target_user] = {"id": channel_id, "name": channel_name, "user_name": user_name, "server_name": server_name}
                        changed = True
                
                if changed:
                    print(f"[Bot Update] Processed initial bulk VC sync for {len(updates)} users.")
                    broadcast_vc_updates()

    except websockets.exceptions.ConnectionClosed:
        pass
    except Exception as e:
        print(f"[Error] {e}")
    finally:
            if user_id:
                remove_user_from_pool(user_id)
                if user_id in active_connections and active_connections[user_id] == websocket:
                    del active_connections[user_id]
                    del user_approved_lists[user_id]
                    if user_id in user_versions:
                        del user_versions[user_id]
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