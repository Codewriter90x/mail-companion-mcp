FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HOME=/tmp
WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
# Explicit allowlist: credentials can never be copied into this image.
COPY config.py mail_client.py server.py test_mail.py ./
RUN python test_mail.py
USER 10001:10001
ENTRYPOINT ["python", "server.py"]
