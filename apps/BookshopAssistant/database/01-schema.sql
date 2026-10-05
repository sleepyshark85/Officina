-- The bookshop schema (APP-04). PostgreSQL runs this on the container's first start, before 02-seed.sql.

create table genres (
    id   integer generated always as identity primary key,
    name text not null unique
);

create table authors (
    id   integer generated always as identity primary key,
    name text not null
);

create table books (
    id             integer generated always as identity primary key,
    title          text not null,
    author_id      integer not null references authors,
    genre_id       integer not null references genres,
    price          numeric(8, 2) not null check (price >= 0),
    published_year integer not null
);

create table stock (
    book_id  integer primary key references books,
    quantity integer not null check (quantity >= 0)
);

create table customers (
    id         integer generated always as identity primary key,
    name       text not null,
    email      text not null unique,
    created_at timestamptz not null default now()
);

create table orders (
    id          integer generated always as identity primary key,
    customer_id integer not null references customers,
    status      text not null check (status in ('placed', 'cancelled')),
    placed_at   timestamptz not null default now(),
    total       numeric(10, 2) not null
);

create table order_lines (
    order_id   integer not null references orders,
    book_id    integer not null references books,
    quantity   integer not null check (quantity > 0),
    unit_price numeric(8, 2) not null,
    primary key (order_id, book_id)
);

create index on books (genre_id);
create index on books (author_id);
create index on orders (customer_id);
