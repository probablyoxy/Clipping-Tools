import discord
import asyncio
import websockets
import json
import os
import sys
import time

CONFIG_FILE = "config.json"
if os.path.exists(CONFIG_FILE):
    with open(CONFIG_FILE, "r") as f:
        config = json.load(f)
        BOT_SECRET_ID = config.get("BOT_SECRET_ID", "")
        DISCORD_TOKEN = config.get("DISCORD_BOT_TOKEN", "")
else:
    print("[Error] config.json not found! Please create it with your token and secret ID.")
    sys.exit()

intents = discord.Intents.default()
intents.voice_states = True
intents.members = True

bot = discord.Client(intents=intents)
ws_connection = None
assigned_guilds = set()

class LinkAppView(discord.ui.View):
    def __init__(self, user_id, app_uuid):
        super().__init__(timeout=None)
        self.user_id = user_id
        self.app_uuid = app_uuid

    @discord.ui.button(label="Approve & Link App", style=discord.ButtonStyle.green)
    async def approve_button(self, interaction: discord.Interaction, button: discord.ui.Button):
        if str(interaction.user.id) != str(self.user_id):
            await interaction.response.send_message("This isn't for you!", ephemeral=True)
            return

        if ws_connection:
            await ws_connection.send(json.dumps({
                "action": "bot_verify_uuid",
                "user_id": self.user_id,
                "app_uuid": self.app_uuid
            }))
            
            button.disabled = True
            button.label = "App Linked!"
            await interaction.response.edit_message(content="✅ **Your Clipping Tools app has been successfully linked!**", view=self)
        else:
            await interaction.response.send_message("Bot is currently disconnected from the server. Try again later.", ephemeral=True)

async def send_guild_sync():
    if not ws_connection: return
    guild_data = {}
    for guild in bot.guilds:
        has_admin = guild.me.guild_permissions.administrator
        vc_count = sum(1 for c in guild.voice_channels if c.permissions_for(guild.me).view_channel)
        guild_data[str(guild.id)] = {"has_admin": has_admin, "vc_count": vc_count}
    
    try:
        await ws_connection.send(json.dumps({
            "action": "bot_guild_sync",
            "bot_id": BOT_SECRET_ID,
            "guilds": guild_data
        }))
    except Exception as e:
        print(f"[Bot] Failed to send guild sync: {e}")

async def connect_to_router():
    global ws_connection
    while True:
        try:
            async with websockets.connect("wss://clip.oxy.pizza", max_size=None) as ws:
                ws_connection = ws
                print("[Bot] Successfully connected to Central Router!")

                await ws.send(json.dumps({"action": "bot_identify", "bot_id": BOT_SECRET_ID}))

                await send_guild_sync()

                while True:
                    msg = await ws.recv()
                    data = json.loads(msg)

                    if data.get("action") == "resolve_ids":
                        client_id = data.get("client_id")
                        users_to_check = data.get("users", [])
                        channels_to_check = data.get("channels", [])

                        resolved_users = {}
                        resolved_channels = {}

                        for uid in users_to_check:
                            try:
                                user = bot.get_user(int(uid)) or await bot.fetch_user(int(uid))
                                if user: resolved_users[str(uid)] = user.name
                            except: pass

                        for cid in channels_to_check:
                            try:
                                channel = bot.get_channel(int(cid)) or await bot.fetch_channel(int(cid))
                                if channel: resolved_channels[str(cid)] = channel.name
                            except: pass

                        await ws.send(json.dumps({
                            "action": "resolved_ids",
                            "client_id": client_id,
                            "users": resolved_users,
                            "channels": resolved_channels
                        }))

                    elif data.get("action") == "try_dm_link":
                        user_id = data.get("user_id")
                        app_uuid = data.get("app_uuid")
                        success = False
                        
                        try:
                            user = bot.get_user(int(user_id)) or await bot.fetch_user(int(user_id))
                            if user:
                                embed = discord.Embed(
                                    title="Link Clipping Tools App",
                                    description="We noticed a new Clipping Tools app trying to connect to your account.\n\nClick the button below to verify and link this app.",
                                    color=discord.Color.green()
                                )
                                view = LinkAppView(user_id, app_uuid)
                                await user.send(embed=embed, view=view)
                                success = True
                        except Exception as e:
                            print(f"[Bot] Could not DM user {user_id}: {e}")
                            
                        await ws.send(json.dumps({
                            "action": "dm_result",
                            "user_id": user_id,
                            "success": success
                        }))

                    elif data.get("action") == "get_all_users":
                        client_id = data.get("client_id")
                        all_users = {}

                        try:
                            client_id_int = int(client_id)
                            for guild in bot.guilds:
                                if guild.get_member(client_id_int):
                                    for member in guild.members:
                                        if not member.bot:
                                            all_users[str(member.id)] = member.name
                        except (TypeError, ValueError):
                            pass

                        await ws.send(json.dumps({
                            "action": "all_users_list",
                            "client_id": client_id,
                            "users": all_users
                        }))
                        
                    elif data.get("action") == "assign_guilds":
                        global assigned_guilds
                        assigned_guilds = set(data.get("guild_ids", []))
                        print(f"[Bot] Assigned to monitor {len(assigned_guilds)} servers exclusively.")

        except Exception as e:
            ws_connection = None
            print(f"[Bot] Disconnected from Router. Retrying in 5s... ({e})")
            await asyncio.sleep(5)

router_task_started = False

@bot.event
async def on_ready():
    global router_task_started
    print(f"Bot logged in as {bot.user}")
    
    if not router_task_started:
        router_task_started = True
        bot.loop.create_task(connect_to_router())

@bot.event
async def on_guild_join(guild):
    print(f"[Bot] Joined new server: {guild.name}. Syncing with router...")
    await send_guild_sync()

@bot.event
async def on_guild_remove(guild):
    print(f"[Bot] Left server: {guild.name}. Syncing with router...")
    await send_guild_sync()

@bot.event
async def on_member_update(before, after):
    if before.id == bot.user.id and before.roles != after.roles:
        await send_guild_sync()

@bot.event
async def on_voice_state_update(member, before, after):
    if not ws_connection:
        return 
        
    guild_id = str(member.guild.id)
    if guild_id not in assigned_guilds:
        return
    
    if before.channel == after.channel:
        return

    channel_id = str(after.channel.id) if after.channel else None
    
    payload = {
        "action": "vc_update",
        "user_id": str(member.id),
        "user_name": member.display_name,
        "channel_id": channel_id,
        "channel_name": after.channel.name if after.channel else None,
        "timestamp": time.time()
    }
    
    try:
        await ws_connection.send(json.dumps(payload))
        print(f"Reported update: {member.name} -> VC: {channel_id}")
    except Exception as e:
        print(f"Failed to send VC update to router: {e}")

bot.run(DISCORD_TOKEN)