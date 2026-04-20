import asyncio
import websockets
import json
import logging
import os
import random
import string
import time
import urllib.parse
import aiohttp
import sys
from aiohttp import web

logging.getLogger("websockets").setLevel(logging.CRITICAL)

BASE_DIR = os.path.dirname(os.path.abspath(__file__))

USERS_DIR = os.path.join(BASE_DIR, "users")
if not os.path.exists(USERS_DIR):
    os.makedirs(USERS_DIR)

verified_uuids = {}
linking_locks = {}

stats_lock = asyncio.Lock()
user_data_locks = {}

def get_user_lock(user_id):
    uid_str = str(user_id)
    if uid_str not in user_data_locks:
        user_data_locks[uid_str] = asyncio.Lock()
    return user_data_locks[uid_str]

def load_all_users():
    for user_id in os.listdir(USERS_DIR):
        user_dir = os.path.join(USERS_DIR, user_id)
        if os.path.isdir(user_dir):
            user_json_path = os.path.join(user_dir, "user.json")
            if os.path.exists(user_json_path):
                with open(user_json_path, "r") as f:
                    data = json.load(f)
                    linking_locks[user_id] = data.get("linking_locked", False)
            
            apps_json_path = os.path.join(user_dir, ".tokens", "apps.json")
            if os.path.exists(apps_json_path):
                with open(apps_json_path, "r") as f:
                    verified_uuids[user_id] = json.load(f)

load_all_users()

async def save_user_data(user_id):
    async with get_user_lock(user_id):
        def _write():
            uid_str = str(user_id)
            user_dir = os.path.join(USERS_DIR, uid_str)
            tokens_dir = os.path.join(user_dir, ".tokens")
            if not os.path.exists(tokens_dir):
                os.makedirs(tokens_dir)
                
            with open(os.path.join(user_dir, "user.json"), "w") as f:
                json.dump({
                    "discord_id": uid_str,
                    "linking_locked": linking_locks.get(uid_str, False)
                }, f)
                
            with open(os.path.join(tokens_dir, "apps.json"), "w") as f:
                json.dump(verified_uuids.get(uid_str, []), f)
        await asyncio.to_thread(_write)

CONFIG_FILE = os.path.join(BASE_DIR, "config.json")
config = {}
if os.path.exists(CONFIG_FILE):
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            config = json.load(f)
            ALLOWED_BOT_IDS = config.get("ALLOWED_BOT_IDS", [])
            PREFERRED_BOT_ID = config.get("PREFERRED_BOT_ID", "")
            print(f"[Startup] Successfully loaded config from: {CONFIG_FILE}")
    except Exception as e:
        print(f"[CRITICAL ERROR] Failed to read config.json: {e}")
else:
    print(f"[CRITICAL ERROR] config.json NOT FOUND AT: {CONFIG_FILE}")
    ALLOWED_BOT_IDS = []
    PREFERRED_BOT_ID = ""

STATS_FILE = os.path.join(BASE_DIR, "stats.json")
if os.path.exists(STATS_FILE):
    with open(STATS_FILE, "r") as f:
        server_stats = json.load(f)
else:
    server_stats = {"clips_synced": 0, "clips_taken": 0}

async def save_stats():
    async with stats_lock:
        def _write():
            with open(STATS_FILE, "w") as f:
                json.dump(server_stats, f)
        await asyncio.to_thread(_write)

active_connections = {}
unverified_connections = {}
connection_attempts = {}
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

auth_listeners = {}
user_server_tokens = {}

def load_server_tokens():
    for user_id in os.listdir(USERS_DIR):
        token_path = os.path.join(USERS_DIR, user_id, ".tokens", ".token.json")
        if os.path.exists(token_path):
            with open(token_path, "r") as f:
                user_server_tokens[user_id] = json.load(f)

load_server_tokens()

async def save_server_token(user_id, token):
    async with get_user_lock(user_id):
        def _write():
            uid_str = str(user_id)
            user_server_tokens[uid_str] = token
            
            tokens_dir = os.path.join(USERS_DIR, uid_str, ".tokens")
            if not os.path.exists(tokens_dir):
                os.makedirs(tokens_dir)
                
            token_path = os.path.join(tokens_dir, ".token.json")
            with open(token_path, "w") as f:
                json.dump(token, f)
        await asyncio.to_thread(_write)

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
    for client_id, apps in list(active_connections.items()):
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
        
        for app_uuid, ws in list(apps.items()):
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
            for app_uuid, ws in list(active_connections[uid].items()):
                try:
                    asyncio.create_task(ws.send(json.dumps(payload)))
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

async def send_custom_message(user_id, title, message):
    if user_id in active_connections:
        for app_uuid, ws in list(active_connections[user_id].items()):
            try:
                await ws.send(json.dumps({
                    "action": "custom_message",
                    "title": title,
                    "message": message
                }))
            except: pass

async def handle_client(websocket):
    client_ip = websocket.remote_address[0] if websocket.remote_address else "Unknown"
    current_time = time.time()
    
    if client_ip not in connection_attempts:
        connection_attempts[client_ip] = []
        
    connection_attempts[client_ip] = [ts for ts in connection_attempts[client_ip] if current_time - ts < 60]
    
    if len(connection_attempts[client_ip]) >= 10:
        print(f"[Rate Limit] Dropped connection from {client_ip} (Too many attempts).")
        try:
            await websocket.send(json.dumps({
                "action": "rate_limited"
            }))
            await asyncio.sleep(0.5)
            await websocket.close(1008, "Rate limited")
        except: pass
        return
        
    connection_attempts[client_ip].append(current_time)

    user_id = None
    is_bot = False

    try:
        async for message in websocket:
            try:
                data = json.loads(message)
            except json.JSONDecodeError:
                continue
            
            action = data.get("action")

            # ==========================================
            # DESKTOP APP MESSAGES
            # ==========================================
            if action == "auth_listen":
                state_code = data.get("state")
                if state_code:
                    old_states = [s for s, ws in auth_listeners.items() if ws == websocket]
                    for s in old_states: del auth_listeners[s]
                    
                    auth_listeners[state_code] = websocket
                print(f"[Auth] App is listening for login completion with state: {state_code}")
                
                redirect_uri = config.get('DISCORD_REDIRECT_URI')
                
                if not redirect_uri:
                    print("[Error] DISCORD_REDIRECT_URI is missing from config.json!")
                    sys.exit(1)
                    
                login_url = redirect_uri.replace("/auth/callback", f"/auth/login?state={state_code}")
                
                try:
                    await websocket.send(json.dumps({
                        "action": "auth_url",
                        "url": login_url
                    }))
                except:
                    pass

            elif action == "identify":
                user_id = data.get("discord_id") or data.get("user_id")
                token_id = data.get("token_id")
                app_uuid = data.get("app_uuid")
                app_version = data.get("version", "Unknown")
                approved_users = data.get("approved_users", [])

                is_outdated = True
                if app_version != "Unknown":
                    try:
                        v_str = app_version.lower().replace("v", "").split("-")[0]
                        parts = [int(x) for x in v_str.split(".")][:3]
                        while len(parts) < 3: parts.append(0)
                        if parts >= [0, 1, 9]:
                            is_outdated = False
                    except:
                        pass
                
                if is_outdated:
                    print(f"[Security] Blocked outdated client {user_id} ({app_version})")
                    try:
                        await websocket.send(json.dumps({
                            "action": "dm_verification_failed"
                        }))
                    except: pass
                    continue
                
                stored_token = user_server_tokens.get(str(user_id))
                if not token_id or stored_token != token_id:
                    print(f"[Security] Blocked unauthorized login attempt for {user_id}")
                    try:
                        await websocket.send(json.dumps({
                            "action": "auth_failed",
                            "reason": "Your login token is invalid or expired. Please sign back in via Discord."
                        }))
                    except: pass
                    continue
                
                if not user_id or not app_uuid:
                    continue

                if app_uuid in verified_uuids.get(user_id, []):
                    if user_id not in active_connections:
                        active_connections[user_id] = {}
                    active_connections[user_id][app_uuid] = websocket
                    user_approved_lists[user_id] = approved_users
                    
                    app_count = len(active_connections[user_id])
                    for a_uuid, a_ws in list(active_connections[user_id].items()):
                        try:
                            asyncio.create_task(a_ws.send(json.dumps({"action": "concurrent_apps", "count": app_count})))
                        except: pass
                        
                    user_versions[user_id] = app_version
                    print(f"[Desktop] User {user_id} connected ({app_version}). Friends list size: {len(approved_users)}")
                    broadcast_vc_updates()
                else:
                    if linking_locks.get(user_id, False):
                        print(f"[Security] Blocked unverified connection for {user_id} (App Linking Locked).")
                        try: asyncio.create_task(websocket.send(json.dumps({"action": "linking_locked"})))
                        except: pass
                        continue

                    if user_id not in unverified_connections:
                        unverified_connections[user_id] = {}
                    unverified_connections[user_id][app_uuid] = {"ws": websocket, "approved_users": approved_users, "version": app_version}
                    print(f"[Desktop] User {user_id} connected with unverified UUID {app_uuid} ({app_version}). Requesting bot link.")
                    
                    try:
                        asyncio.create_task(websocket.send(json.dumps({"action": "awaiting_approval"})))
                    except: pass

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
                            for app_uuid, ws in list(active_connections[client_id].items()):
                                try: await ws.send(json.dumps({"action": "all_users_list", "client_id": client_id, "users": {}}))
                                except: pass
                    else:
                        pending_all_users_requests[client_id] = {"bots_left": len(active_bots), "users": {}}
                        for bot_ws in list(active_bots.keys()):
                            try: await bot_ws.send(message)
                            except: pending_all_users_requests[client_id]["bots_left"] -= 1

            elif action == "trigger":
                if not user_id: continue
                app_uuid = data.get("app_uuid")

                if app_uuid not in verified_uuids.get(user_id, []):
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
                        for app_uuid, target_ws in list(active_connections[target_user].items()):
                            payload = {
                                "action": "sync_clip",
                                "sender_id": user_id
                            }
                            try:
                                await target_ws.send(json.dumps(payload))
                            except: pass
                        print(f"[Router] Sent clip signal from {user_id} -> {target_user}")
                        server_stats["clips_taken"] += 1
                
                await save_stats()


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
                            for app_uuid, ws in list(active_connections[member].items()):
                                try: await ws.send(json.dumps({"action": "pool_closed"}))
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
                                for app_uuid, ws in list(active_connections[target_uid].items()):
                                    try: await ws.send(json.dumps({"action": "pool_kicked"}))
                                    except: pass
                            remove_user_from_pool(target_uid)
                        elif manage_action == "ban":
                            pools[pool_code]["banned"].append(target_uid)
                            if target_uid in active_connections:
                                for app_uuid, ws in list(active_connections[target_uid].items()):
                                    try: await ws.send(json.dumps({"action": "pool_banned"}))
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
                    if target_user not in verified_uuids: verified_uuids[target_user] = []
                    if app_uuid not in verified_uuids[target_user]: verified_uuids[target_user].append(app_uuid)
                    linking_locks[target_user] = True
                    await save_user_data(target_user)
                    print(f"[Bot] Verified UUID for user {target_user} and locked linking.")
                    
                    if target_user in unverified_connections and app_uuid in unverified_connections[target_user]:
                        conn_data = unverified_connections[target_user].pop(app_uuid)
                        if not unverified_connections[target_user]:
                            del unverified_connections[target_user]
                            
                        if target_user not in active_connections:
                            active_connections[target_user] = {}
                        active_connections[target_user][app_uuid] = conn_data["ws"]
                        user_approved_lists[target_user] = conn_data["approved_users"]
                        user_versions[target_user] = conn_data.get("version", "Unknown")
                        print(f"[Desktop] Moved {target_user} to active connections.")
                        
                        if "cached_resolved_ids" in conn_data:
                            try: await conn_data["ws"].send(conn_data["cached_resolved_ids"])
                            except: pass
                        
                        app_count = len(active_connections[target_user])
                        for a_uuid, a_ws in list(active_connections[target_user].items()):
                            try:
                                await a_ws.send(json.dumps({"action": "concurrent_apps", "count": app_count}))
                            except: pass
                            
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

            elif action == "bot_toggle_lock":
                if websocket not in active_bots: continue
                target_user = data.get("user_id")
                new_state = data.get("state")
                linking_locks[target_user] = new_state
                await save_user_data(target_user)
                status = "locked" if new_state else "unlocked"
                print(f"[Bot] User {target_user} {status} app linking.")
                try:
                    await websocket.send(json.dumps({
                        "action": "bot_dm_reply",
                        "user_id": target_user,
                        "message": f"App linking is now **{status}**." if new_state else f"App linking is now **{status}**. New apps can request to link."
                    }))
                except: pass

            elif action == "bot_reset_uuid":
                if websocket not in active_bots: continue
                target_user = data.get("user_id")
                if target_user in verified_uuids:
                    verified_uuids[target_user] = []
                    await save_user_data(target_user)
                    print(f"[Bot] User {target_user} reset their UUID.")
                    msg = "Your UUID has been reset. No apps can connect using your account until you link a new one."
                    
                    if target_user in active_connections:
                        for app_uuid, ws in list(active_connections[target_user].items()):
                            try: asyncio.create_task(ws.close())
                            except: pass
                    if target_user in unverified_connections:
                        for u_app_uuid, u_data in list(unverified_connections[target_user].items()):
                            try: asyncio.create_task(u_data["ws"].close())
                            except: pass
                else:
                    msg = "You do not have a linked UUID to reset."
                    
                try:
                    await websocket.send(json.dumps({
                        "action": "bot_dm_reply",
                        "user_id": target_user,
                        "message": msg
                    }))
                except: pass

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
                    for app_uuid, ws in list(active_connections[target_client].items()):
                        try: await ws.send(message)
                        except: pass
                elif target_client in unverified_connections:
                    for app_uuid, conn_data in unverified_connections[target_client].items():
                        conn_data["cached_resolved_ids"] = message
					
            elif action == "all_users_list":
                if websocket not in active_bots: continue
                target_client = data.get("client_id")
                
                if target_client in pending_all_users_requests:
                    pending_all_users_requests[target_client]["users"].update(data.get("users", {}))
                    pending_all_users_requests[target_client]["bots_left"] -= 1
                    
                    if pending_all_users_requests[target_client]["bots_left"] <= 0:
                        combined_users = pending_all_users_requests[target_client]["users"]
                        if target_client in active_connections:
                            for app_uuid, ws in list(active_connections[target_client].items()):
                                try: 
                                    await ws.send(json.dumps({
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
        keys_to_remove = [state for state, ws in auth_listeners.items() if ws == websocket]
        for key in keys_to_remove: del auth_listeners[key]

        if user_id:
            disconnected_app = None
            if user_id in active_connections:
                for uid, ws in list(active_connections[user_id].items()):
                    if ws == websocket:
                        disconnected_app = uid
                        break
                        
            if disconnected_app:
                del active_connections[user_id][disconnected_app]
                if active_connections[user_id]:
                    app_count = len(active_connections[user_id])
                    for a_uuid, a_ws in list(active_connections[user_id].items()):
                        try:
                            asyncio.create_task(a_ws.send(json.dumps({"action": "concurrent_apps", "count": app_count})))
                        except: pass
                
            if user_id in active_connections and not active_connections[user_id]:
                remove_user_from_pool(user_id)
                del active_connections[user_id]
                if user_id in user_approved_lists: del user_approved_lists[user_id]
                if user_id in user_versions: del user_versions[user_id]
                if user_id in user_last_clip_time: del user_last_clip_time[user_id]
                if user_id in pending_dm_requests: del pending_dm_requests[user_id]
                if user_id in pending_all_users_requests: del pending_all_users_requests[user_id]
                print(f"[Desktop] User {user_id} disconnected.")
                broadcast_vc_updates()
                        
            if user_id in unverified_connections:
                disconnected_unverified = None
                for u_app_uuid, u_data in list(unverified_connections[user_id].items()):
                    if u_data["ws"] == websocket:
                        disconnected_unverified = u_app_uuid
                        break
                if disconnected_unverified:
                    del unverified_connections[user_id][disconnected_unverified]
                    if not unverified_connections[user_id]:
                        del unverified_connections[user_id]
                    print(f"[Desktop] Unverified User {user_id} app {disconnected_unverified} disconnected.")
        elif is_bot:
            if websocket in active_bots:
                del active_bots[websocket]
            if websocket in bot_guild_stats:
                del bot_guild_stats[websocket]
                asyncio.create_task(assign_guilds_to_bots())
            print("[Bot] A Discord Bot disconnected.")

async def auth_login(request):
    state = request.query.get('state')
    if not state:
        return web.Response(text="Missing state parameter", status=400)
    
    client_id = config.get('DISCORD_CLIENT_ID', '1480703669555957791')
    redirect_uri = config.get('DISCORD_REDIRECT_URI', '')
    
    discord_auth_url = (
        f"https://discord.com/api/oauth2/authorize"
        f"?client_id={client_id}"
        f"&redirect_uri={urllib.parse.quote(redirect_uri)}"
        f"&response_type=code"
        f"&scope=identify"
        f"&state={state}"
    )
    raise web.HTTPFound(discord_auth_url)

async def auth_callback(request):
    code = request.query.get('code')
    state = request.query.get('state')

    if not code or not state:
        return web.Response(text="Missing code or state", status=400)

    client_id = config.get('DISCORD_CLIENT_ID', '1480703669555957791')
    client_secret = config.get('DISCORD_CLIENT_SECRET', '')
    redirect_uri = config.get('DISCORD_REDIRECT_URI', '')

    async with aiohttp.ClientSession() as session:
        data = {
            'client_id': client_id,
            'client_secret': client_secret,
            'grant_type': 'authorization_code',
            'code': code,
            'redirect_uri': redirect_uri
        }
        headers = {'Content-Type': 'application/x-www-form-urlencoded'}
        async with session.post("https://discord.com/api/oauth2/token", data=data, headers=headers) as resp:
            if resp.status != 200:
                return web.Response(text="Failed to get token from Discord", status=400)
            token_data = await resp.json()
            access_token = token_data['access_token']

        headers = {'Authorization': f"Bearer {access_token}"}
        async with session.get("https://discord.com/api/users/@me", headers=headers) as resp:
            if resp.status != 200:
                return web.Response(text="Failed to get user info", status=400)
            user_data = await resp.json()
            discord_id = str(user_data['id'])

    existing_token = user_server_tokens.get(discord_id)
    if existing_token:
        server_token = existing_token
    else:
        server_token = ''.join(random.choices(string.ascii_letters + string.digits, k=64))
        await save_server_token(discord_id, server_token)

    if state in auth_listeners:
        ws = auth_listeners[state]
        success_msg = json.dumps({
            "action": "auth_success",
            "discord_id": discord_id,
            "token_id": server_token
        })
        try:
            await ws.send(success_msg)
        except Exception:
            pass
        del auth_listeners[state]

    html = """
    <html><body style='background:#36393f; color:white; font-family:sans-serif; text-align:center; padding-top:50px;'>
    <h2 style='color:#43b581'>Success!</h2><p>You can close this window and return to the app.</p>
    <script>window.close();</script>
    </body></html>
    """
    return web.Response(text=html, content_type='text/html')

async def main():
    print("Starting Clipping Tools Central Router...")
    
    app = web.Application()
    app.router.add_get('/auth/login', auth_login)
    app.router.add_get('/auth/callback', auth_callback)
    runner = web.AppRunner(app)
    await runner.setup()
    
    http_port = config.get("HTTP_PORT", 4244)
    ws_port = config.get("WS_PORT", 4242)
    
    site = web.TCPSite(runner, '0.0.0.0', http_port)
    await site.start()
    print(f"HTTP Auth Server listening on port {http_port}")

    async with websockets.serve(handle_client, "0.0.0.0", ws_port, max_size=None):
        await asyncio.Future()

if __name__ == "__main__":
    asyncio.run(main())