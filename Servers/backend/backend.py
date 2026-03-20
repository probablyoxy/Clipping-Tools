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
else:
    print("[Warning] config.json not found! Bots will not be able to authenticate.")
    ALLOWED_BOT_IDS = []

active_connections = {} 
unverified_connections = {}

user_approved_lists = {}

vc_map = {} 

active_bots = set()

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
                    for bot_ws in list(active_bots):
                        try:
                            await bot_ws.send(json.dumps({
                                "action": "request_link",
                                "user_id": user_id,
                                "app_uuid": app_uuid
                            }))
                        except: pass

            elif action == "update_users":
                if user_id:
                    user_approved_lists[user_id] = data.get("approved_users", [])
                    print(f"[Desktop] User {user_id} updated their friend list.")
                    broadcast_vc_updates()

            elif action == "resolve_ids":
                for bot_ws in list(active_bots):
                    try: await bot_ws.send(message)
                    except: pass

            elif action == "get_all_users":
                for bot_ws in list(active_bots):
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
                print(f"[Desktop] User {user_id} triggered a clip in VC {sender_channel}.")

                for target_user, c_data in vc_map.items():
                    if c_data.get("id") == sender_channel and target_user != user_id:

                        if target_user in active_connections:
                            target_ws = active_connections[target_user]
                            payload = {
                                "action": "sync_clip",
                                "sender_id": user_id
                            }
                            await target_ws.send(json.dumps(payload))
                            print(f"[Router] Sent clip signal from {user_id} -> {target_user}")


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
                    active_bots.add(websocket)
                    print(f"[Bot] A Discord Bot authenticated with ID: {bot_id}")
                else:
                    print(f"[Security] Rejected bot connection with invalid ID: {bot_id}")

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
                if channel_id:
                    vc_map[target_user] = {"id": channel_id, "name": channel_name, "user_name": user_name}
                    print(f"[Bot Update] {target_user} ({user_name}) joined VC {channel_name}.")
                else:
                    if target_user in vc_map:
                        del vc_map[target_user]
                    print(f"[Bot Update] {target_user} left VC.")
                
                broadcast_vc_updates()

    except websockets.exceptions.ConnectionClosed:
        pass
    except Exception as e:
        print(f"[Error] {e}")
    finally:
            if user_id:
                if user_id in active_connections:
                    del active_connections[user_id]
                    del user_approved_lists[user_id]
                    print(f"[Desktop] User {user_id} disconnected.")
                    broadcast_vc_updates()
                            
                if user_id in unverified_connections:
                    del unverified_connections[user_id]
                    print(f"[Desktop] Unverified User {user_id} disconnected.")
            elif is_bot:
                if websocket in active_bots:
                    active_bots.remove(websocket)
                print("[Bot] A Discord Bot disconnected.")

async def main():
    print("Starting Clipping Tools Central Router on port 8765...")
    async with websockets.serve(handle_client, "0.0.0.0", 8765, max_size=None):
        await asyncio.Future()

if __name__ == "__main__":
    asyncio.run(main())