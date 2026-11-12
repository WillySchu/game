-- 0007: how much of each trader offer each player has bought in its current restock period, for offers with
-- limited stock (traders.json). period is the restock period's number: whole minutes since 1970-01-01 UTC,
-- divided by the offer's restock_minutes. A row from an earlier period counts as nothing bought.
--
-- Items bought and money paid for sales are ordinary item_instances rows with origin 'purchase'; items sold
-- take the state 'sold', and money spent the state 'consumed'. Both were allowed from the start.

CREATE TABLE trader_purchases (
    player_id TEXT    NOT NULL REFERENCES players (id),
    trader_id TEXT    NOT NULL,
    offer_id  TEXT    NOT NULL,
    period    INTEGER NOT NULL,
    bought    INTEGER NOT NULL CHECK (bought >= 0),
    PRIMARY KEY (player_id, trader_id, offer_id)
);
