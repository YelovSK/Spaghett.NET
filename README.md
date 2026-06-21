# Q: Why are there a bunch of unnecessary projects for a stupid spaghett Discord bot? You trying to look cool? Compensating for something?

# A: Yes.

## Docker

Create `.env` from `.env.example`, set `DISCORD_TOKEN`, then run:

```sh
docker compose up -d --build
```

SQLite data is stored in `./data/data.db` via the compose volume.
