CREATE TABLE false_positive_users
(
    user_id BIGINT PRIMARY KEY
        REFERENCES "user" (id) ON DELETE CASCADE
);

CREATE TABLE false_positive_messages
(
    chat_id    BIGINT NOT NULL,
    message_id INT    NOT NULL,
    PRIMARY KEY (chat_id, message_id),
    FOREIGN KEY (chat_id, message_id) REFERENCES "message" (chat_id, message_id) ON DELETE CASCADE
);

CREATE TABLE false_negative_messages
(
    chat_id    BIGINT NOT NULL,
    message_id INT    NOT NULL,
    PRIMARY KEY (chat_id, message_id),
    FOREIGN KEY (chat_id, message_id) REFERENCES "message" (chat_id, message_id) ON DELETE CASCADE
);
