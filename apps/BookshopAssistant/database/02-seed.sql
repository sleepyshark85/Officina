-- Seed data, deterministic: 12 genres, 60 authors, 480 books with stock, 40 customers and 80 past orders. The catalogue
-- is large enough for a broad search to return a 10-15k-token result (APP-17). Alice Martin is customer 1 (APP-09).

insert into genres (name)
select unnest(array['Fantasy', 'Science Fiction', 'Mystery', 'Romance', 'History', 'Biography', 'Poetry', 'Horror',
                    'Children', 'Cookery', 'Travel', 'Philosophy']);

insert into authors (name)
select first || ' ' || last
from unnest(array['Margaret', 'Tomas', 'Ingrid', 'Kwame', 'Lucia', 'Hiro', 'Nadia', 'Owen', 'Priya', 'Rafael'])
         with ordinality as f(first, i)
cross join unnest(array['Okafor', 'Lindqvist', 'Moreau', 'Castellano', 'Brennan', 'Takeda'])
         with ordinality as l(last, j)
order by i, j;

-- Titles combine 24 adjectives and 20 nouns, so all 480 are distinct.
insert into books (title, author_id, genre_id, price, published_year)
select 'The ' || (array['Silver', 'Hidden', 'Burning', 'Silent', 'Golden', 'Broken', 'Last', 'Winter', 'Crimson', 'Distant',
                        'Hollow', 'Painted', 'Iron', 'Forgotten', 'Wandering', 'Glass', 'Midnight', 'Northern', 'Salt',
                        'Velvet', 'Paper', 'Copper', 'Emerald', 'Quiet'])[(n - 1) / 20 + 1]
           || ' ' || (array['Tide', 'Crown', 'Garden', 'Archive', 'Harbour', 'Lantern', 'Orchard', 'Kingdom', 'River',
                            'Clockmaker', 'Library', 'Mountain', 'Cartographer', 'Feast', 'Station', 'Island', 'Letter',
                            'Bridge', 'Tower', 'Voyage'])[(n - 1) % 20 + 1],
       (n * 13) % 60 + 1,
       (n * 7) % 12 + 1,
       5 + ((n * 37) % 2600) / 100.0,
       1950 + (n * 17) % 76
from generate_series(1, 480) as n
order by n;

-- About one book in thirteen is out of stock.
insert into stock (book_id, quantity)
select id, (id * 5 + id / 7) % 13 from books;

-- The cheapest fantasy book is out of stock, so "in stock" matters in APP-09's request.
update stock set quantity = 0 where book_id = 72;

insert into customers (name, email, created_at)
values ('Alice Martin', 'alice.martin@example.com', timestamptz '2024-03-02 10:00:00+00');

insert into customers (name, email, created_at)
select first || ' ' || last,
       lower(first || '.' || last) || '@example.com',
       timestamptz '2024-01-01 09:00:00+00' + n * interval '9 days'
from (select first, last, row_number() over (order by i, j) as n
      from unnest(array['Ben', 'Chloe', 'Daniel', 'Emma', 'Farid', 'Grace', 'Henrik', 'Isla', 'Jonas', 'Keiko', 'Leo',
                        'Maya', 'Noah'])
               with ordinality as f(first, i)
      cross join unnest(array['Hughes', 'Novak', 'Adeyemi']) with ordinality as l(last, j)) as names
order by n;

-- Past orders of one to three lines; one in eight is cancelled. They leave the stock above as it is.
insert into orders (customer_id, status, placed_at, total)
select (n * 7) % 40 + 1,
       case when n % 8 = 0 then 'cancelled' else 'placed' end,
       timestamptz '2025-01-06 11:00:00+00' + n * interval '4 days 3 hours',
       0
from generate_series(1, 80) as n
order by n;

insert into order_lines (order_id, book_id, quantity, unit_price)
select o.id, b.id, (o.id + line) % 2 + 1, b.price
from orders o
cross join generate_series(0, 2) as line
join books b on b.id = (o.id * 53 + line * 101) % 480 + 1
where line < o.id % 3 + 1;

update orders o
set total = (select sum(quantity * unit_price) from order_lines l where l.order_id = o.id);
