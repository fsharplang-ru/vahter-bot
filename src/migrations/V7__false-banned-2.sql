DROP TABLE false_positive_messages;
CREATE TABLE false_positive_messages
(
    id BIGINT PRIMARY KEY
        REFERENCES banned (id) ON DELETE CASCADE
);
