import discord
import asyncio
import websockets
import json
import os
import sys

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

async def connect_to_router():
    global ws_connection
    while True:
        try:
            async with websockets.connect("wss://clip.oxy.pizza", max_size=None) as ws:
                ws_connection = ws
                print("[Bot] Successfully connected to Central Router!")

                await ws.send(json.dumps({"action": "bot_identify", "bot_id": BOT_SECRET_ID}))

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

                    elif data.get("action") == "request_link":
                        user_id = data.get("user_id")
                        app_uuid = data.get("app_uuid")
                        
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
                        except Exception as e:
                            print(f"[Bot] Could not DM user {user_id}: {e}")

                    elif data.get("action") == "get_all_users":
                        client_id = data.get("client_id")
                        all_users = {}

                        for u in bot.users:
                            if not u.bot:
                                all_users[str(u.id)] = u.name

                        await ws.send(json.dumps({
                            "action": "all_users_list",
                            "client_id": client_id,
                            "users": all_users
                        }))

        except Exception as e:
            ws_connection = None
            print(f"[Bot] Disconnected from Router. Retrying in 5s... ({e})")
            await asyncio.sleep(5)

@bot.event
async def on_ready():
    print(f"Bot logged in as {bot.user}")
    bot.loop.create_task(connect_to_router())

@bot.event
async def on_voice_state_update(member, before, after):
    if not ws_connection:
        return 
    
    if before.channel == after.channel:
        return

    channel_id = str(after.channel.id) if after.channel else None
    
    payload = {
        "action": "vc_update",
        "user_id": str(member.id),
        "channel_id": channel_id
    }
    
    try:
        await ws_connection.send(json.dumps(payload))
        print(f"Reported update: {member.name} -> VC: {channel_id}")
    except Exception as e:
        print(f"Failed to send VC update to router: {e}")

bot.run(DISCORD_TOKEN)