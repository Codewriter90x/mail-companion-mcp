FROM python:3.12-slim
ENV ARUBA_MAIL_DB_PATH=/data/followups.sqlite3 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HOME=/tmp
RUN mkdir /data && chown 10001:10001 /data && chmod 700 /data
WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
# Explicit allowlist: credentials can never be copied into this image.
COPY config.py mail_client.py server.py http_transport.py test_mail.py test_http.py followups.py test_followups.py ./
RUN python test_mail.py && python test_http.py && python test_followups.py
USER 10001:10001
ENTRYPOINT ["python", "server.py"]
