-- A large catalogue beside the seed: 1,000,000 books, 20,000 authors and 200,000 customers, shaped like the seed. Each
-- name ends in a word of its own (from md5 of its number), so a search can be rare or common.
insert into authors (name)
select (array['Margaret', 'Tomas', 'Ingrid', 'Kwame', 'Lucia', 'Hiro', 'Nadia', 'Owen', 'Priya', 'Rafael'])[n % 10 + 1] || ' '
       || (array['Okafor', 'Lindqvist', 'Moreau', 'Castellano', 'Brennan', 'Takeda'])[n % 6 + 1] || ' '
       || initcap(substr(md5('a' || n), 1, 6))
from generate_series(1, 20000) as n;

insert into books (title, author_id, genre_id, price, published_year)
select 'The ' || (array['Silver', 'Hidden', 'Burning', 'Silent', 'Golden', 'Broken', 'Last', 'Winter', 'Crimson', 'Distant',
                        'Hollow', 'Painted', 'Iron', 'Forgotten', 'Wandering', 'Glass', 'Midnight', 'Northern', 'Salt',
                        'Velvet', 'Paper', 'Copper', 'Emerald', 'Quiet'])[n % 24 + 1]
       || ' ' || (array['Tide', 'Crown', 'Garden', 'Archive', 'Harbour', 'Lantern', 'Orchard', 'Kingdom', 'River',
                        'Clockmaker', 'Library', 'Mountain', 'Cartographer', 'Feast', 'Station', 'Island', 'Letter',
                        'Bridge', 'Tower', 'Voyage'])[n % 20 + 1]
       || ' of ' || initcap(substr(md5('b' || n), 1, 6)),
       (n * 13) % 20060 + 1,
       (n * 7) % 12 + 1,
       5 + ((n * 37) % 2600) / 100.0,
       1950 + (n * 17) % 76
from generate_series(1, 1000000) as n;

insert into stock (book_id, quantity)
select id, (id * 5 + id / 7) % 13 from books where id not in (select book_id from stock);

insert into customers (name, email, created_at)
select first || ' ' || initcap(substr(md5('c' || n), 1, 7)),
       lower(first) || '.' || substr(md5('c' || n), 1, 7) || '.' || n || '@example.com',
       timestamptz '2024-01-01 09:00:00+00' + (n % 900) * interval '1 day'
from (select n, (array['Ben', 'Chloe', 'Daniel', 'Emma', 'Farid', 'Grace', 'Henrik', 'Isla', 'Jonas', 'Keiko', 'Leo', 'Maya',
                       'Noah'])[n % 13 + 1] as first
      from generate_series(1, 200000) as n) as numbered;
